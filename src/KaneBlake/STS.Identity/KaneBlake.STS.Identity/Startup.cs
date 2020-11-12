using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;
using KaneBlake.Basis.Services;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Dashboard.Resources;
using IdentityModel.Client;
using IdentityServer4.Configuration;
using KaneBlake.AspNetCore.Extensions.Middleware;
using KaneBlake.AspNetCore.Extensions.MVC;
using KaneBlake.Basis.Domain.Repositories;
using KaneBlake.Basis.Common.Cryptography;
using KaneBlake.STS.Identity.Common;
using KaneBlake.STS.Identity.Common.IdentityServer4Config;
using KaneBlake.STS.Identity.HangfireCustomDashboard;
using KaneBlake.STS.Identity.Infrastruct.Context;
using KaneBlake.STS.Identity.Infrastruct.Entities;
using KaneBlake.STS.Identity.Infrastruct.Repository;
using KaneBlake.STS.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;
using KaneBlake.Basis.Common.Serialization;
using Microsoft.AspNetCore.Mvc.Razor;
using System.Globalization;
using Microsoft.AspNetCore.Localization;
using KaneBlake.AspNetCore.Extensions.MVC.Filters;
using System.Security.Cryptography.X509Certificates;

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

            IMvcBuilder builder = services.AddControllersWithViews(options =>
            {
                // it doesn't require tokens for requests made using the following safe HTTP methods: GET, HEAD, OPTIONS, and TRACE
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
                options.Filters.Add<InjectResultActionFilter>();
                options.Conventions.Add(new InvalidModelStateFilterConvention());
            })
                .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
                .AddDataAnnotationsLocalization()
                .AddJsonOptions(options => options.JsonSerializerOptions.Configure())
                .ConfigureApiBehaviorOptions(options =>
                {
                    // avoid adding duplicate Convention: InvalidModelStateFilterConvention
                    options.SuppressModelStateInvalidFilter = false;
                    options.InvalidModelStateResponseFactory = context =>
                    {
                        var response = ServiceResponse.BadRequest(new SerializableModelError(context.ModelState));
                        var traceId = Activity.Current?.Id ?? context.HttpContext?.TraceIdentifier;
                        response.TryAddTraceId(traceId);
                        var result = new ObjectResult(response);
                        result.ContentTypes.Add("application/problem+json");
                        result.ContentTypes.Add("application/problem+xml");
                        return result;
                    };
                });

#if DEBUG
            if (Env.IsDevelopment())
            {
                builder.AddRazorRuntimeCompilation();
            }
#endif

            services.AddPortableObjectLocalization(options => options.ResourcesPath = "Localization");

            services.Configure<RequestLocalizationOptions>(options =>
            {
                var supportedCultures = new[] { "zh-CN", "en-US" };

                options.SetDefaultCulture(supportedCultures[0])
                    .AddSupportedCultures(supportedCultures)
                    .AddSupportedUICultures(supportedCultures);
            });


            services.AddResponseCompression((options) =>
            {
                // https://resources.infosecinstitute.com/the-breach-attack/
                // Disable compression on dynamically generated pages which over secure connections to avoid security problems.
                options.EnableForHttps = false;
            });


            // Microsoft.Extensions.Caching.StackExchangeRedis
            //services.AddStackExchangeRedisCache(options =>
            //{
            //    options.Configuration = "localhost:5000";
            //    options.InstanceName = "LightBlogCache";
            //});


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


            services.AddScoped<EncryptFormResourceFilterAttribute>();
            services.AddSingleton(AppInfo.Instance.Certificate);
            services.AddScoped<InjectResultActionFilter>();


            var jsPath = DashboardRoutes.Routes.Contains("/js[0-9]+") ? "/js[0-9]+" : "/js[0-9]{3}";
            //DashboardRoutes.Routes.Append(jsPath, new EmbeddedResourceDispatcher("application/javascript", Assembly.GetExecutingAssembly(),$"{typeof(Startup).Namespace}.HangfireCustomDashboard.hangfire.custom.js"));
            DashboardRoutes.Routes.Append(jsPath, new StaticFileDispatcher("application/javascript", "js/hangfire.custom.js", Env.WebRootFileProvider));
            services.AddHangfire(x => x.UseSqlServerStorage(AppOptions.IdentityDB));
            services.AddHangfireServer();


            services.AddTransient<IJobManageService, JobManageService>();
            services.AddSingleton<JobEntryResolver>();


            services.AddSingleton<IActionContextAccessor, ActionContextAccessor>();


            services.AddHostedService<ConsumeScopedServiceHostedService>();
            services.AddScoped<IScopedProcessingService, ScopedProcessingService>();

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (!env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // errorHandlingPath:�ı�����·����,�����м���ٴδ�������
                //app.UseExceptionHandler("/error_handle");
                // use RequestDelegate to handle exception:��Ҫ�ֶ��� IServiceProvider �н��������ķ���
                //app.UseExceptionHandler(new ExceptionHandlerOptions()
                //{
                //    ExceptionHandler = async context => await context.Response.WriteAsync("Unhandled exception occurred!")
                //});

                app.UseExceptionHandler(errorApp => errorApp.UseMiddleware<ExceptionHandlerCustomMiddleware>());

                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseMiddleware<RequestBufferingMiddleware>();
            app.UseHttpsRedirection();
            // Use Ngix to enable Compression
            app.UseResponseCompression();


            // Set up custom content types - associating file extension to MIME type
            app.UseStaticFiles();


            app.UseCookiePolicy();

            // ����IdentityServer������м��
            // ��û��ʹ��.NET Core��Session�м��
            // ����������Cookie
            // "key": "idsrv"
            // "key": "idsrv.session"
            app.UseIdentityServer();

            // Write streamlined request completion events, instead of the more verbose ones from the framework.
            // To use the default framework request logging instead, remove this line and set the "Microsoft"
            // level in appsettings.json to "Information".
            app.UseSerilogRequestLogging();

            app.UseRouting();
            app.UseResponseCaching();

            app.UseAuthorization();

            app.UseHangfireDashboard(AppInfo.HangfirePath, new DashboardOptions
            {
                Authorization = new[] { new HangfireDashboardAuthorizationFilter(AppInfo.HangfireLoginUrl) }
            });

            app.UseRequestLocalization();

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
                options.UserInteraction = new UserInteractionOptions { LoginUrl = AppInfo.LoginUrl };
                // �������� token �ɹ���, д�� token �� Issuer,����Ϊ"null"����֤ Issuer. ���� IdSrv �����ս�� '/.well-known/openid-configuration' �鿴 Issuer
                // ����Ϊ null�����ַ������߲�����ʱ: IssuerUri ��Ĭ��ΪΪ��Ŀ������ַ
                options.IssuerUri = "null";
                options.Authentication.CookieLifetime = TimeSpan.FromHours(0.5);
                // �û������� ��֤�ɹ�֮�󣬽�ƾ֤д��cookie
                // ��cookie��Ч���ڲ���Ҫ���µ�¼, ֱ��ͨ��ƾ֤��ȡcode ???
            })
                .AddSigningCredential(AppInfo.Instance.Certificate)// ���� token ���ܵ�֤��
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


    public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private readonly string _hangfireLoginUrl;

        public HangfireDashboardAuthorizationFilter(string hangfireLoginUrl)
        {
            _hangfireLoginUrl = hangfireLoginUrl ?? throw new ArgumentNullException(nameof(hangfireLoginUrl));
        }

        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            if (!httpContext.User.Identity.IsAuthenticated)
            {
                httpContext.Response.Redirect(_hangfireLoginUrl);
            }

            // Allow all authenticated users to see the Dashboard (potentially dangerous).
            return true;
        }
    }
}
