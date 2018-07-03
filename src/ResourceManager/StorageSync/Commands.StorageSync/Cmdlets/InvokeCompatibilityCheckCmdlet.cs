﻿namespace Microsoft.Azure.Commands.StorageSync.Evaluation.Cmdlets
{
    using System.Collections.Generic;
    using System.IO;
    using System.Management.Automation;
    using System.Net;
    using OutputWriters;
    using Validations.NamespaceValidations;
    using Validations.SystemValidations;
    using System;
    using System.Diagnostics;
    using Interfaces;
    using Validations;
    using Models;
    using ResourceManager.Common;

    [Cmdlet(
        VerbsLifecycle.Invoke, "AzureRmStorageSyncCompatibilityCheck", 
        DefaultParameterSetName="PathBased")]
    [OutputType(typeof(PSValidationResult))]
    public class InvokeCompatibilityCheckCmdlet : AzureRMCmdlet, ICmdlet
    {
        #region Fields and Properties
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "PathBased")]
        public string Path { get; set; }

        [Parameter]
        public PSCredential Credential { get; set; }

        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ComputerNameBased")]
        public string ComputerName { get; set; }

        private string ComputerNameValue {
            get
            {
                if (!string.IsNullOrEmpty(this.Path))
                {
                    var afsPath = new AfsPath(this.Path);
                    if (afsPath.ComputerName != null)
                    {
                        return afsPath.ComputerName;
                    }

                    return "localhost";
                }

                return this.ComputerName;
            }
        }

        [Parameter]
        public SwitchParameter SkipSystemChecks { get; set; }

        [Parameter(ParameterSetName = "PathBased")]
        public SwitchParameter SkipNamespaceChecks { get; set; }

        [Parameter]
        public SwitchParameter Quiet { get; set; }

        private bool CanRunNamespaceChecks => !string.IsNullOrEmpty(this.Path);
        private bool CanRunEstimation => this.CanRunNamespaceChecks;
        private bool CanRunSystemChecks => !string.IsNullOrEmpty(this.ComputerNameValue);

        private TimeSpan MaximumDurationOfNamespaceEstimation => TimeSpan.FromSeconds(30);

        #endregion

        #region Protected methods
        protected override void ProcessRecord()
        {
            Configuration configuration = new Configuration();

            // prepare namespace validations
            var namespaceValidations = new List<INamespaceValidation>()
            {
                new InvalidFilenameValidation(configuration),
                new FilenamesCharactersValidation(configuration),
                new MaximumFileSizeValidation(configuration),
                new MaximumPathLengthValidation(configuration),
                new MaximumFilenameLengthValidation(configuration),
                new MaximumTreeDepthValidation(configuration),
                new MaximumDatasetSizeValidation(configuration),
            };

            // prepare system validations
            var systemValidations = new List<ISystemValidation>
            {
                new OSVersionValidation(configuration),
            };

            if (this.CanRunNamespaceChecks)
            {
                systemValidations.Add(new FileSystemValidation(configuration, this.Path));
            }

            // construct validation descriptions
            List<IValidationDescription> validationDescriptions = new List<IValidationDescription>();
            namespaceValidations.ForEach(o => validationDescriptions.Add((IValidationDescription)o));
            systemValidations.ForEach(o => validationDescriptions.Add((IValidationDescription)o));

            // output writers
            TextSummaryOutputWriter summaryWriter = new TextSummaryOutputWriter(new AfsConsoleWriter(), validationDescriptions);
            PsObjectsOutputWriter psObjectsWriter = new PsObjectsOutputWriter(this);

            this.WriteVerbose($"Path = {this.Path}");
            this.WriteVerbose($"ComputerName = {this.ComputerName}");
            this.WriteVerbose($"ComputerNameValue = {this.ComputerNameValue}");
            this.WriteVerbose($"CanRunNamespaceChecks = {this.CanRunNamespaceChecks}");
            this.WriteVerbose($"CanRunSystemChecks = {this.CanRunSystemChecks}");
            this.WriteVerbose($"CanRunEstimation = {this.CanRunEstimation}");
            this.WriteVerbose($"SkipNamespaceChecks = {this.SkipNamespaceChecks}");
            this.WriteVerbose($"SkipSystemChecks = {this.SkipSystemChecks}");
            this.WriteVerbose($"Quiet = {this.Quiet}");
            this.WriteVerbose($"NumberOfSystemChecks = {systemValidations.Count}");
            this.WriteVerbose($"NumberOfNamespaceChecks = {namespaceValidations.Count}");

            long totalObjectsToScan = 0;
            if (this.CanRunEstimation && !this.SkipNamespaceChecks.ToBool())
            {
                IProgressReporter progressReporter = new NamespaceEstimationProgressReporter(this);
                progressReporter.Show();
                progressReporter.AddSteps(1);

                Stopwatch stopwatch = Stopwatch.StartNew();
                INamespaceInfo namespaceInfoEstimation = new NamespaceEnumerator().Run(
                    new AfsDirectoryInfo(this.Path),
                    this.MaximumDurationOfNamespaceEstimation);
                stopwatch.Stop();

                totalObjectsToScan += namespaceInfoEstimation.NumberOfDirectories + namespaceInfoEstimation.NumberOfFiles;
                progressReporter.CompleteStep();
                progressReporter.Complete();
                string namespaceCompleteness = namespaceInfoEstimation.IsComplete ? "complete" : "incomplete";
                TimeSpan duration = stopwatch.Elapsed;

                this.WriteVerbose($"Namespace estimation took {duration.TotalSeconds:F3} seconds and is {namespaceCompleteness}");
            }
            else
            {
                this.WriteVerbose("Skipping estimation.");
            }

            if (this.CanRunSystemChecks && !this.SkipSystemChecks.ToBool())
            {
                IProgressReporter progressReporter = new SystemCheckProgressReporter(this);
                progressReporter.Show();

                progressReporter.AddSteps(systemValidations.Count);
                Stopwatch stopwatch = Stopwatch.StartNew();
                this.PerformSystemChecks(systemValidations, progressReporter, this, summaryWriter, psObjectsWriter);
                stopwatch.Stop();
                progressReporter.Complete();
                TimeSpan duration = stopwatch.Elapsed;

                this.WriteVerbose($"System checks took {duration.TotalSeconds:F3} seconds");
            }
            else
            {
                this.WriteVerbose("Skipping system checks.");
            }

            INamespaceInfo namespaceInfo = null;
            if (this.CanRunNamespaceChecks && !this.SkipNamespaceChecks.ToBool())
            {
                IProgressReporter progressReporter = new NamespaceScanProgressReporter(this);
                progressReporter.Show();
                progressReporter.AddSteps(totalObjectsToScan);

                Stopwatch stopwatch = Stopwatch.StartNew();
                namespaceInfo = this.PerformNamespaceChecks(namespaceValidations, progressReporter, this, summaryWriter, psObjectsWriter);
                stopwatch.Stop();
                progressReporter.Complete();

                TimeSpan duration = stopwatch.Elapsed;
                long namespaceFileCount = namespaceInfo.NumberOfFiles;
                double fileThroughput = namespaceFileCount > 0 ? duration.TotalMilliseconds / namespaceFileCount : 0.0;
                this.WriteVerbose($"Namespace scan took {duration.TotalSeconds:F3} seconds with throughput of {fileThroughput:F3} milliseconds per file");
            }
            else
            {
                this.WriteVerbose("Skipping namespace checks.");
            }

            if (!this.Quiet.ToBool())
            {
                summaryWriter.WriteReport(this.ComputerNameValue, namespaceInfo);
            }
            else
            {
                this.WriteVerbose("Skipping report.");
            }
        }
        #endregion

        #region Private methods
        private void PerformSystemChecks(IList<ISystemValidation> validations, IProgressReporter progressReporter, ICmdlet cmdlet, TextSummaryOutputWriter summaryWriter, PsObjectsOutputWriter psObjectsWriter)
        {
            PowerShellCommandRunner commandRunner = null;

            try
            {
                commandRunner = new PowerShellCommandRunner(this.ComputerNameValue, this.Credential);
            }
            catch
            {
                string user = this.Credential == null ? "the current user" : this.Credential.UserName;
                this.WriteWarning(
                    $"Establishing management service connection with host '{this.ComputerNameValue}' as {user} didn't work." + Environment.NewLine +
                    $"Ensure {user} has administrative rights and that the process is running with administrative permissions." + Environment.NewLine +
                    $"You can also use -SkipSystemChecks switch to skip system requirements checks.");
                throw;
            }

            var outputWriters = new List<IOutputWriter>
            {
                summaryWriter,
                psObjectsWriter
            };

            SystemValidationsProcessor systemChecksProcessor = new SystemValidationsProcessor(commandRunner, validations, outputWriters, progressReporter);

            systemChecksProcessor.Run();
        }

        private INamespaceInfo PerformNamespaceChecks(IList<INamespaceValidation> validations, IProgressReporter progressReporter, ICmdlet cmdlet, TextSummaryOutputWriter summaryWriter, PsObjectsOutputWriter psObjectsWriter)
        {
            INamespaceInfo result = null;

            if (this.Credential != null)
            {
                using (UncNetworkConnector connector = new UncNetworkConnector())
                {
                    NetworkCredential networkCredential = this.Credential.GetNetworkCredential();
                    if (connector.NetUseWithCredentials(this.Path, networkCredential.UserName, networkCredential.Domain, networkCredential.Password))
                    {
                        result = this.StorageEval(validations, progressReporter, cmdlet, summaryWriter, psObjectsWriter);
                    }
                    else
                    {
                        string errorMessage = connector.GetLastError();
                        WriteError(new ErrorRecord(new Exception(errorMessage), errorMessage, ErrorCategory.ConnectionError, this.Path));
                    }
                }
            }
            else
            {
                result = this.StorageEval(validations, progressReporter, cmdlet, summaryWriter, psObjectsWriter);
            }

            return result;
        }

        private INamespaceInfo StorageEval(IList<INamespaceValidation> validations, IProgressReporter progressReporter, ICmdlet cmdlet, TextSummaryOutputWriter summaryWriter, PsObjectsOutputWriter psObjectsWriter)
        {
            IDirectoryInfo root = new AfsDirectoryInfo(this.Path);

            var outputWriters = new List<IOutputWriter>
            {
                psObjectsWriter,
                summaryWriter
            };

            NamespaceValidationsProcessor validationsProcessor = new NamespaceValidationsProcessor(validations, outputWriters, progressReporter);

            List<INamespaceEnumeratorListener> namespaceEnumeratorListeners = new List<INamespaceEnumeratorListener>
            {
                validationsProcessor,
            };

            NamespaceEnumerator namespaceEnumerator = new NamespaceEnumerator(namespaceEnumeratorListeners);

            var namespaceInfo = namespaceEnumerator.Run(root);

            return namespaceInfo;
        }
        #endregion
    }
}