using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IdentityServer4.Configuration;
using KaneBlake.Basis.Domain.Repositories;
using KaneBlake.Basis.Extensions.Cryptography;
using KaneBlake.STS.Identity.Common;
using KaneBlake.STS.Identity.Common.IdentityServer4Config;
using KaneBlake.STS.Identity.Infrastruct.Context;
using KaneBlake.STS.Identity.Infrastruct.Entities;
using KaneBlake.STS.Identity.Infrastruct.Repository;
using KaneBlake.STS.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KaneBlake.STS.Identity
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Env = env;
            Configuration = configuration;
            AppOptions = Configuration.Get<AppOptions>();
        }

        public IConfiguration Configuration { get; }
        public AppOptions AppOptions { get; }
        public IWebHostEnvironment Env { get; set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            IMvcBuilder builder = services.AddControllersWithViews(options=> {
                // it doesn't require tokens for requests made using the following safe HTTP methods: GET, HEAD, OPTIONS, and TRACE
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });
            #if DEBUG
            if (Env.IsDevelopment())
            {
                builder.AddRazorRuntimeCompilation();
            }
            #endif

            // https://resources.infosecinstitute.com/the-breach-attack/
            // EnableForHttps = false��
            // Disable compression on dynamically generated pages which over secure connections to avoid security problems.
            services.AddResponseCompression();

            ConfigureIdentityServer(services);


            services.AddDbContext<UserDbContext>(options =>
            {
                options.UseSqlServer(AppOptions.IdentityDB,
                    sqlServerOptionsAction: sqlOptions =>
                    {
                        sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                    });
            });

            services.AddTransient<IRepository<User, int>, UserRepository>();
            services.AddTransient<IUserService<User>, UserService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            // Use Ngix to enable Compression
            app.UseResponseCompression();

            app.UseStaticFiles();



            app.UseCookiePolicy();

            // ����IdentityServer������м��
            // ��û��ʹ��.NET Core��Session�м��
            // ����������Cookie
            // "key": "idsrv"
            // "key": "idsrv.session"
            app.UseIdentityServer();

            app.UseRouting();

            app.UseAuthorization();// ��Ȩ

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapDefaultControllerRoute();
            });
        }



        /// <summary>
        /// ���� IdentityServer �����
        /// nuget��: IdentityServer4 IdentityServer4.EntityFramework
        /// 1.ʵ���û���֤��½
        /// 2.�û���֤�ɹ���ͨ����������API����Ȩ��
        /// </summary>
        /// <param name="services"></param>
        private void ConfigureIdentityServer(IServiceCollection services)
        {
            services.Configure<CookiePolicyOptions>(options =>
            {
                options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
                options.OnAppendCookie = cookieContext =>
                    CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
                options.OnDeleteCookie = cookieContext =>
                    CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
            });

            var migrationsAssembly = typeof(Startup).GetTypeInfo().Assembly.GetName().Name;
            services.AddIdentityServer(options =>
            {
                // �����û���¼�Ľ���ҳ��,Ĭ��Ϊ /Account/Login
                options.UserInteraction = new UserInteractionOptions { LoginUrl = "/Account/Login" };
                // �������� token �ɹ���, д�� token �� Issuer,����Ϊ"null"����֤ Issuer. ���� IdSrv �����ս�� '/.well-known/openid-configuration' �鿴 Issuer
                // ����Ϊ null�����ַ������߲�����ʱ: IssuerUri ��Ĭ��ΪΪ��Ŀ������ַ
                options.IssuerUri = "null";
                options.Authentication.CookieLifetime = TimeSpan.FromHours(0.5);
                // �û������� ��֤�ɹ�֮�󣬽�ƾ֤д��cookie
                // ��cookie��Ч���ڲ���Ҫ���µ�¼, ֱ��ͨ��ƾ֤��ȡcode ???
            })
                .AddSigningCredential(CertificateExtensions.GetX509Certificate(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Certs", "IdentityServerCredential.pfx")
                ))// ���� token ���ܵ�֤��
                .AddConfigurationStore(options =>// �־û���Դ���ͻ���
                {
                    options.ConfigureDbContext = builder => builder.UseSqlServer(AppOptions.IdentityDB,
                            sqlServerOptionsAction: sqlOptions =>
                            {
                                // ����ConfigurationDbContext������ʱǨ�ư󶨵�Assembly
                                sqlOptions.MigrationsAssembly(migrationsAssembly);
                                sqlOptions.EnableRetryOnFailure(maxRetryCount: 15, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                            });
                })
                .AddOperationalStore(options =>// �־û� ��Ȩ�롢ˢ�����ơ��û���Ȩ��Ϣconsents
                {
                    options.ConfigureDbContext = builder => builder.UseSqlServer(AppOptions.IdentityDB,
                            sqlServerOptionsAction: sqlOptions =>
                            {
                                sqlOptions.MigrationsAssembly(migrationsAssembly);
                                sqlOptions.EnableRetryOnFailure(maxRetryCount: 15, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                            });
                    options.EnableTokenCleanup = true;// �����Զ��������
                })
                .AddProfileService<MyProfileService>()
                .AddResourceOwnerValidator<MyResourceOwnerPasswordValidator>();


        }

        private void CheckSameSite(HttpContext httpContext, CookieOptions options)
        {
            if (options.SameSite == SameSiteMode.None)
            {
                var userAgent = httpContext.Request.Headers["User-Agent"].ToString();
                // TODO: Use your User Agent library of choice here.
                options.SameSite = SameSiteMode.Unspecified;
            }
        }
    }
}
