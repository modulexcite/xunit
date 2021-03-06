using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Microsoft.Build.Framework;
using Xunit.Abstractions;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Xunit.Runner.MSBuild
{
    public class xunit : MSBuildTask, ICancelableTask
    {
        volatile bool cancel;
        readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();
        XunitFilters filters;
        int? maxParallelThreads;
        bool? parallelizeAssemblies;
        bool? parallelizeTestCollections;
        readonly TeamCityDisplayNameFormatter teamCityDisplayNameFormatter = new TeamCityDisplayNameFormatter();

        public xunit()
        {
            ShadowCopy = true;
            TeamCity = Environment.GetEnvironmentVariable("TEAMCITY_PROJECT_NAME") != null;
        }

        [Required]
        public ITaskItem[] Assemblies { get; set; }

        public bool DiagnosticMessages { get; set; }

        public string ExcludeTraits { get; set; }

        [Output]
        public int ExitCode { get; protected set; }

        public ITaskItem Html { get; set; }

        public string IncludeTraits { get; set; }

        public string MaxParallelThreads { get; set; }

        protected bool NeedsXml
        {
            get { return Xml != null || XmlV1 != null || Html != null; }
        }

        public bool ParallelizeAssemblies { set { parallelizeAssemblies = value; } }

        public bool ParallelizeTestCollections { set { parallelizeTestCollections = value; } }

        // To be used by the xUnit.net team for diagnostic purposes only
        public bool SerializeTestCases { get; set; }

        public bool ShadowCopy { get; set; }

        public bool TeamCity { get; set; }

        protected XunitFilters Filters
        {
            get
            {
                if (filters == null)
                {
                    var traitParser = new TraitParser(msg => Log.LogWarning(msg));
                    filters = new XunitFilters();
                    traitParser.Parse(IncludeTraits, filters.IncludedTraits);
                    traitParser.Parse(ExcludeTraits, filters.ExcludedTraits);
                }

                return filters;
            }
        }

        public bool Verbose { get; set; }

        public string WorkingFolder { get; set; }

        public ITaskItem Xml { get; set; }

        public ITaskItem XmlV1 { get; set; }

        public void Cancel()
        {
            cancel = true;
        }

        protected virtual IFrontController CreateFrontController(string assemblyFilename, string configFileName, IMessageSink diagnosticsMessageSink)
        {
            return new XunitFrontController(assemblyFilename, configFileName, ShadowCopy, diagnosticMessageSink: diagnosticsMessageSink);
        }

        protected virtual XmlTestExecutionVisitor CreateVisitor(string assemblyFileName, XElement assemblyElement)
        {
            if (TeamCity)
                return new TeamCityVisitor(Log, assemblyElement, () => cancel, displayNameFormatter: teamCityDisplayNameFormatter);

            return new StandardOutputVisitor(Log, assemblyElement, Verbose, () => cancel, completionMessages);
        }

        public override bool Execute()
        {
            RemotingUtility.CleanUpRegisteredChannels();
            XElement assembliesElement = null;
            var environment = string.Format("{0}-bit .NET {1}", IntPtr.Size * 8, Environment.Version);

            if (NeedsXml)
                assembliesElement = new XElement("assemblies");

            switch (MaxParallelThreads)
            {
                case null:
                case "default":
                    break;

                case "unlimited":
                    maxParallelThreads = 0;
                    break;

                default:
                    int threadValue;
                    if (!int.TryParse(MaxParallelThreads, out threadValue) || threadValue < 0)
                    {
                        Log.LogError("MaxParallelThreads value '{0}' is invalid: must be 'default', 'unlimited', or a positive number", MaxParallelThreads);
                        return false;
                    }

                    maxParallelThreads = threadValue;
                    break;
            }

            var originalWorkingFolder = Directory.GetCurrentDirectory();

            using (AssemblyHelper.SubscribeResolve())
            {
                Log.LogMessage(MessageImportance.High, "xUnit.net MSBuild runner ({0})", environment);

                var testAssemblyPaths = Assemblies.Select(assembly =>
                {
                    var assemblyFileName = assembly.GetMetadata("FullPath");
                    var configFileName = assembly.GetMetadata("ConfigFile");
                    if (configFileName != null && configFileName.Length == 0)
                        configFileName = null;

                    return Tuple.Create(assemblyFileName, configFileName, ConfigReader.Load(assemblyFileName, configFileName));
                }).ToList();

                if (WorkingFolder != null)
                    Directory.SetCurrentDirectory(WorkingFolder);

                var clockTime = Stopwatch.StartNew();

                if (!parallelizeAssemblies.HasValue)
                    parallelizeAssemblies = testAssemblyPaths.All(tuple => tuple.Item3.ParallelizeAssemblyOrDefault);

                if (parallelizeAssemblies.GetValueOrDefault())
                {
                    var tasks = testAssemblyPaths.Select(path => Task.Run(() => ExecuteAssembly(path.Item1, path.Item2, path.Item3)));
                    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                    foreach (var assemblyElement in results.Where(result => result != null))
                        assembliesElement.Add(assemblyElement);
                }
                else
                {
                    foreach (var path in testAssemblyPaths)
                    {
                        var assemblyElement = ExecuteAssembly(path.Item1, path.Item2, path.Item3);
                        if (assemblyElement != null)
                            assembliesElement.Add(assemblyElement);
                    }
                }

                clockTime.Stop();

                if (completionMessages.Count > 0)
                {
                    Log.LogMessage(MessageImportance.High, "=== TEST EXECUTION SUMMARY ===");

                    var totalTestsRun = completionMessages.Values.Sum(summary => summary.Total);
                    var totalTestsFailed = completionMessages.Values.Sum(summary => summary.Failed);
                    var totalTestsSkipped = completionMessages.Values.Sum(summary => summary.Skipped);
                    var totalTime = completionMessages.Values.Sum(summary => summary.Time).ToString("0.000s");
                    var totalErrors = completionMessages.Values.Sum(summary => summary.Errors);
                    var longestAssemblyName = completionMessages.Keys.Max(key => key.Length);
                    var longestTotal = totalTestsRun.ToString().Length;
                    var longestFailed = totalTestsFailed.ToString().Length;
                    var longestSkipped = totalTestsSkipped.ToString().Length;
                    var longestTime = totalTime.Length;
                    var longestErrors = totalErrors.ToString().Length;

                    foreach (var message in completionMessages.OrderBy(m => m.Key))
                        Log.LogMessage(MessageImportance.High,
                                       "   {0}  Total: {1}, Errors: {2}, Failed: {3}, Skipped: {4}, Time: {5}",
                                       message.Key.PadRight(longestAssemblyName),
                                       message.Value.Total.ToString().PadLeft(longestTotal),
                                       message.Value.Errors.ToString().PadLeft(longestErrors),
                                       message.Value.Failed.ToString().PadLeft(longestFailed),
                                       message.Value.Skipped.ToString().PadLeft(longestSkipped),
                                       message.Value.Time.ToString("0.000s").PadLeft(longestTime));

                    if (completionMessages.Count > 1)
                        Log.LogMessage(MessageImportance.High,
                                       "   {0}         {1}          {2}          {3}           {4}        {5}" + Environment.NewLine +
                                       "           {6} {7}          {8}          {9}           {10}        {11} ({12})",
                                       " ".PadRight(longestAssemblyName),
                                       "-".PadRight(longestTotal, '-'),
                                       "-".PadRight(longestErrors, '-'),
                                       "-".PadRight(longestFailed, '-'),
                                       "-".PadRight(longestSkipped, '-'),
                                       "-".PadRight(longestTime, '-'),
                                       "GRAND TOTAL:".PadLeft(longestAssemblyName),
                                       totalTestsRun,
                                       totalErrors,
                                       totalTestsFailed,
                                       totalTestsSkipped,
                                       totalTime,
                                       clockTime.Elapsed.TotalSeconds.ToString("0.000s"));
                }
            }

            Directory.SetCurrentDirectory(WorkingFolder ?? originalWorkingFolder);

            if (NeedsXml)
            {
                if (Xml != null)
                    assembliesElement.Save(Xml.GetMetadata("FullPath"));

                if (XmlV1 != null)
                    Transform("xUnit1.xslt", assembliesElement, XmlV1);

                if (Html != null)
                    Transform("HTML.xslt", assembliesElement, Html);
            }

            return ExitCode == 0;
        }

        XElement CreateAssemblyXElement()
        {
            return NeedsXml ? new XElement("assembly") : null;
        }

        protected virtual XElement ExecuteAssembly(string assemblyFileName, string configFileName, TestAssemblyConfiguration configuration)
        {
            if (cancel)
                return null;

            var assemblyElement = CreateAssemblyXElement();

            try
            {
                if (DiagnosticMessages)
                    configuration.DiagnosticMessages = true;

                var discoveryOptions = TestFrameworkOptions.ForDiscovery(configuration);
                var executionOptions = TestFrameworkOptions.ForExecution(configuration);
                if (maxParallelThreads.HasValue && maxParallelThreads.Value > -1)
                    executionOptions.SetMaxParallelThreads(maxParallelThreads);
                if (parallelizeTestCollections.HasValue)
                    executionOptions.SetDisableParallelization(!parallelizeTestCollections);

                var assemblyDisplayName = Path.GetFileNameWithoutExtension(assemblyFileName);
                if (configuration.DiagnosticMessagesOrDefault)
                    Log.LogMessage(MessageImportance.High, "  Discovering: {0} (method display = {1}, parallel test collections = {2}, max threads = {3})",
                                   assemblyDisplayName,
                                   discoveryOptions.GetMethodDisplayOrDefault(),
                                   !executionOptions.GetDisableParallelizationOrDefault(),
                                   executionOptions.GetMaxParallelThreadsOrDefault());
                else
                    Log.LogMessage(MessageImportance.High, "  Discovering: {0}", assemblyDisplayName);

                var diagnosticMessageVisitor = new DiagnosticMessageVisitor(Log, assemblyDisplayName, configuration.DiagnosticMessagesOrDefault);

                using (var controller = CreateFrontController(assemblyFileName, configFileName, diagnosticMessageVisitor))
                using (var discoveryVisitor = new TestDiscoveryVisitor())
                {
                    controller.Find(includeSourceInformation: false, discoveryMessageSink: discoveryVisitor, discoveryOptions: discoveryOptions);
                    discoveryVisitor.Finished.WaitOne();

                    Log.LogMessage(MessageImportance.High, "  Discovered:  {0}", assemblyDisplayName);

                    var filteredTestCases = discoveryVisitor.TestCases.Where(Filters.Filter).ToList();
                    if (filteredTestCases.Count == 0)
                        completionMessages.TryAdd(assemblyDisplayName, new ExecutionSummary());
                    else
                    {
                        if (SerializeTestCases)
                            filteredTestCases = filteredTestCases.Select(controller.Serialize).Select(controller.Deserialize).ToList();

                        using (var resultsVisitor = CreateVisitor(assemblyFileName, assemblyElement))
                        {
                            controller.RunTests(filteredTestCases, resultsVisitor, executionOptions);
                            resultsVisitor.Finished.WaitOne();

                            if (resultsVisitor.Failed != 0)
                                ExitCode = 1;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var e = ex;

                while (e != null)
                {
                    Log.LogError("{0}: {1}", e.GetType().FullName, e.Message);

                    foreach (var stackLine in e.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                        Log.LogError(stackLine);

                    e = e.InnerException;
                }

                ExitCode = -1;
            }

            return assemblyElement;
        }

        static void Transform(string resourceName, XNode xml, ITaskItem outputFile)
        {
            var xmlTransform = new XslCompiledTransform();

            using (var writer = XmlWriter.Create(outputFile.GetMetadata("FullPath"), new XmlWriterSettings { Indent = true }))
            using (var xsltReader = XmlReader.Create(typeof(xunit).Assembly.GetManifestResourceStream("Xunit.Runner.MSBuild." + resourceName)))
            using (var xmlReader = xml.CreateReader())
            {
                xmlTransform.Load(xsltReader);
                xmlTransform.Transform(xmlReader, writer);
            }
        }
    }
}
