﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Karambolo.PO;
using Microsoft.CodeAnalysis;

namespace KaneBlake.Build.Core.Localization
{
    public class LocalizableEntryWriter : IVisitor
    {
        public int Order { get; set; } = int.MaxValue;

        public async Task Visit(DataStructure dataStructure)
        {
            await Task.CompletedTask;

            var language = Thread.CurrentThread.CurrentCulture.Name;
            var projectName = dataStructure.Project.Name;
            var projectPath = dataStructure.ProjectDirectory;
            var localizerEntries = dataStructure.LocalizerEntries;
            var POFilePath = Path.Combine(projectPath, language + ".po");

            POCatalog catalog = null;
            if (File.Exists(POFilePath))
            {
                using var sr = new StreamReader(POFilePath, Encoding.UTF8);
                var parser = new POParser(POParserSettings.Default);
                var result = parser.Parse(sr);
                if (result.Success)
                {
                    catalog = result.Catalog;
                    foreach (var r in catalog)
                    {
                        r.Comments.Clear();
                    }
                }
                else
                {
                    var diagnostics = result.Diagnostics;
                    // examine diagnostics, display an error, etc...
                }
            }
            if (catalog == null)
            {
                catalog = new POCatalog
                {
                    Encoding = Encoding.UTF8.BodyName,
                    PluralFormCount = 1,
                    PluralFormSelector = "0",
                    Language = language
                };

                var assembly = typeof(IVisitor).Assembly;
                catalog.Headers = new Dictionary<string, string>
                {
                    { "PO-Revision-Date", DateTime.UtcNow.ToString() },
                    { "Project-Id-Version", projectName },
                    { "X-Crowdin-Generator", $"Generated by {assembly.GetName().Name} {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion}" },
                };
            }
            HashSet<POKey> sets = new HashSet<POKey>();
            foreach (var entry in localizerEntries)
            {
                var key = new POKey(entry.Id, null, entry.ContextId);
                sets.Add(key);
                if (catalog.TryGetValue(key, out var POEntry))
                {
                    if (!POEntry.Comments.OfType<POExtractedComment>().Any(c => c.Text.Equals(entry.SourceCode)))
                    {
                        POEntry.Comments.Add(new POExtractedComment { Text = entry.SourceCode });
                    }

                    var referenceComment = POEntry.Comments.OfType<POReferenceComment>().FirstOrDefault();
                    if (referenceComment == null)
                    {
                        POEntry.Comments.Add(new POReferenceComment { References = new List<POSourceReference>() { POSourceReference.Parse(entry.SourceReference) } });
                    }
                    else
                    {
                        var sourceReference = POSourceReference.Parse(entry.SourceReference);
                        if (!referenceComment.References.Any(r => r.FilePath.Equals(sourceReference.FilePath) && r.Line.Equals(sourceReference.Line)))
                        {
                            referenceComment.References.Add(sourceReference);
                        }

                    }

                }
                else
                {
                    POEntry = new POSingularEntry(key)
                    {
                        Comments = new List<POComment>()
                        {
                            new POReferenceComment { References = new List<POSourceReference>() { POSourceReference.Parse(entry.SourceReference) } },
                            new POExtractedComment { Text = entry.SourceCode },
                        }
                    };

                    catalog.Add(POEntry);
                }
            }

            var keys = catalog.Keys.ToList();
            keys.Where(k => !sets.Contains(k)).ToList().ForEach(k => catalog.Remove(k));

            if (catalog.Headers.ContainsKey("PO-Revision-Date"))
            {
                catalog.Headers["PO-Revision-Date"] = DateTime.UtcNow.ToString();
            }

            var generator = new POGenerator(POGeneratorSettings.Default);

            using var sw = new StreamWriter(POFilePath, false, Encoding.UTF8);
            generator.Generate(sw, catalog);
        }
    }
}
