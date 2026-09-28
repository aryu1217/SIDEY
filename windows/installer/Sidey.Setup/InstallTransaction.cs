using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Sidey.Installer
{
    internal static class InstallTransactionProgram
    {
        private const int InvalidArguments = 64;
        private const int RetryableCleanupFailure = 10;
        // Recover exit codes are a contract with Sidey.Setup.nsi. Keep 1 as the
        // unclassified/legacy failure and reserve 5 for an ExecWait launch error.
        private const int RecoveryParentFailure = 70;
        private const int RecoveryStateFailure = 71;
        private const int RecoveryRollbackFailure = 72;
        private const int RecoveryStagingFailure = 73;
        private const int RecoveryCommittedFailure = 74;
        private const int RecoveryRegistryFailure = 75;
        private const int RecoveryAccessDenied = 76;
        private const int RecoveryIoFailure = 77;
        private const int RecoveryInitializationFailure = 78;
        private const int LegacyRelocationEligible = 79;

        private sealed class InsecureTransactionParentException : InvalidOperationException
        {
            public InsecureTransactionParentException(string message) : base(message) { }
        }

        [STAThread]
        private static int Main(string[] arguments)
        {
            TransactionOptions options = null;
            InstallTransaction transaction = null;
            try
            {
                options = TransactionOptions.Parse(arguments);
                transaction = new InstallTransaction(options);
                int result = transaction.Run();
                if (transaction.LastFailure != null)
                {
                    WriteFailureDiagnostic(options, transaction, transaction.LastFailure);
                }
                return result;
            }
            catch (ArgumentException exception) when (options == null)
            {
                WriteFailureDiagnostic(
                    new TransactionOptions
                    {
                        Action = "Parse",
                        LogPath = FindDiagnosticLogPath(arguments),
                    },
                    "parse.arguments",
                    null,
                    exception);
                Console.Error.WriteLine(exception.Message);
                return InvalidArguments;
            }
            catch (Exception exception)
            {
                if (transaction != null && transaction.EarlierFailure != null)
                {
                    WriteFailureDiagnostic(
                        options,
                        transaction.EarlierFailureStep,
                        transaction,
                        transaction.EarlierFailure);
                }
                WriteFailureDiagnostic(options, transaction, exception);
                Console.Error.WriteLine(exception.Message);
                return FailureExitCode(options, transaction, exception);
            }
        }

        private static string FindDiagnosticLogPath(string[] arguments)
        {
            if (arguments == null)
            {
                return null;
            }
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], "--log-path", StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }
            return null;
        }

        private static int FailureExitCode(
            TransactionOptions options,
            InstallTransaction transaction,
            Exception exception)
        {
            if (options == null || !string.Equals(
                options.Action, "Recover", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (exception is UnauthorizedAccessException || exception is SecurityException)
            {
                return RecoveryAccessDenied;
            }
            if (exception is IOException)
            {
                return RecoveryIoFailure;
            }
            if (transaction == null)
            {
                return RecoveryInitializationFailure;
            }

            switch (transaction.Operation)
            {
                case "recover.parent-security": return RecoveryParentFailure;
                case "recover.read-state": return RecoveryStateFailure;
                case "recover.inspect-orphan":
                case "recover.rollback": return RecoveryRollbackFailure;
                case "recover.remove-staging": return RecoveryStagingFailure;
                case "recover.complete-committed": return RecoveryCommittedFailure;
                case "recover.pending-location": return RecoveryRegistryFailure;
                default: return 1;
            }
        }

        private static void WriteFailureDiagnostic(
            TransactionOptions options,
            InstallTransaction transaction,
            Exception exception)
        {
            WriteFailureDiagnostic(
                options,
                transaction == null ? "initialize" : transaction.Step,
                transaction,
                exception);
        }

        private static void WriteFailureDiagnostic(
            TransactionOptions options,
            string step,
            InstallTransaction transaction,
            Exception exception)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.LogPath))
            {
                return;
            }

            try
            {
                Win32Exception nativeException = exception as Win32Exception;
                // Keep support logs useful without copying paths or account data from
                // exception messages into a file that users may attach to an issue.
                File.AppendAllText(options.LogPath,
                    "[InstallTransaction]" + Environment.NewLine
                    + "action=" + options.Action + Environment.NewLine
                    + "operation=" + (transaction == null ? "initialize" : transaction.Operation)
                    + Environment.NewLine
                    + "step=" + step + Environment.NewLine
                    + "exception=" + exception.GetType().Name + Environment.NewLine
                    + "hresult=0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + "nativeCode=" + (nativeException == null
                        ? string.Empty
                        : nativeException.NativeErrorCode.ToString(CultureInfo.InvariantCulture))
                    + Environment.NewLine + Environment.NewLine,
                    Encoding.ASCII);
            }
            catch
            {
                // Reporting must never replace the original transaction failure.
            }
        }

        private sealed class TransactionOptions
        {
            public string Action;
            public string InstallDirectory;
            public string StagingDirectory;
            public string RollbackDirectory;
            public string ProductVersion;
            public string UpdateVersion;
            public string LogPath;
            public bool AllowUserWritableParentForTests;

            public static TransactionOptions Parse(string[] arguments)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool allowTests = false;
                for (int index = 0; index < arguments.Length; index++)
                {
                    string name = arguments[index];
                    if (string.Equals(
                        name,
                        "--allow-user-writable-parent-for-tests",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (allowTests)
                        {
                            throw new ArgumentException("A transaction option was specified more than once.");
                        }
                        allowTests = true;
                        continue;
                    }
                    if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                    {
                        throw new ArgumentException("Invalid SIDEY install transaction arguments.");
                    }
                    if (values.ContainsKey(name))
                    {
                        throw new ArgumentException("A transaction option was specified more than once.");
                    }
                    values.Add(name, arguments[++index]);
                }

                var result = new TransactionOptions
                {
                    Action = Required(values, "--action"),
                    InstallDirectory = Required(values, "--install-directory"),
                    StagingDirectory = Required(values, "--staging-directory"),
                    RollbackDirectory = Required(values, "--rollback-directory"),
                    ProductVersion = Required(values, "--product-version"),
                    UpdateVersion = Required(values, "--update-version"),
                    LogPath = values.ContainsKey("--log-path") ? values["--log-path"] : null,
                    AllowUserWritableParentForTests = allowTests,
                };
                if (values.Count != (result.LogPath == null ? 6 : 7)
                    || (result.LogPath != null && string.IsNullOrWhiteSpace(result.LogPath))
                    || !InstallTransaction.IsKnownAction(result.Action))
                {
                    throw new ArgumentException("Invalid SIDEY install transaction arguments.");
                }
                return result;
            }

            private static string Required(Dictionary<string, string> values, string name)
            {
                string value;
                if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Missing SIDEY install transaction option: " + name);
                }
                return value;
            }
        }

        private sealed class PreviousRegistration
        {
            public bool Managed;
            public bool Existed;
            public string ProductVersion = string.Empty;
            public string UpdateVersion = string.Empty;
            public string Language = string.Empty;
            public string Location = string.Empty;
        }

        private sealed class TransactionState
        {
            public int SchemaVersion;
            public string Phase;
            public string CompletionId;
            public string ProductVersion;
            public string UpdateVersion;
            public string InstallDirectory;
            public string StagingDirectory;
            public string RollbackDirectory;
            public bool PreviousInstallExisted;
            public PreviousRegistration PreviousRegistration;
        }

        private sealed class InstallTransaction
        {
            // A stopped framework-dependent host or a security scanner can keep
            // the previous Runtime tree busy briefly. Keep replacement bounded.
            private const int ActivationMoveAttempts = 20;
            private const int ActivationMoveRetryDelayMilliseconds = 250;
            private const string TransactionRegistryPath = @"Software\SIDEY\InstallerTransaction";
            private const string InstallerRegistryPath = @"Software\SIDEY\Installer";
            private const string UninstallRegistryPath =
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SIDEY";
            private const string ProtocolRegistryPath = @"Software\Classes\sidey";
            private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

            private readonly string action;
            private readonly string installPath;
            private readonly string stagingPath;
            private readonly string rollbackPath;
            private readonly string productVersion;
            private readonly string updateVersion;
            private readonly string parentPath;
            private readonly string expectedStagingPrefix;
            private readonly string statePath;
            private readonly bool allowUserWritableParentForTests;
            private PreviousRegistration previousRegistration;
            private bool previousInstallExisted;
            private string transactionStagingPath;
            private string completionId;

            public string Operation { get; private set; }
            public string Step { get; private set; }
            public Exception LastFailure { get; private set; }
            public Exception EarlierFailure { get; private set; }
            public string EarlierFailureStep { get; private set; }

            public InstallTransaction(TransactionOptions options)
            {
                Step = "initialize.paths";
                action = options.Action;
                installPath = Normalize(options.InstallDirectory);
                stagingPath = Normalize(options.StagingDirectory);
                rollbackPath = Normalize(options.RollbackDirectory);
                productVersion = options.ProductVersion;
                updateVersion = options.UpdateVersion;
                allowUserWritableParentForTests = options.AllowUserWritableParentForTests;

                DirectoryInfo parent = Directory.GetParent(installPath);
                string root = Path.GetPathRoot(installPath);
                if (parent == null || PathComparer.Equals(installPath, Normalize(root)))
                {
                    throw new InvalidOperationException(
                        "The SIDEY install directory cannot be a drive root.");
                }
                parentPath = Normalize(parent.FullName);
                expectedStagingPrefix = installPath + ".sidey-staging-";
                statePath = installPath + ".sidey-transaction.json";
                previousRegistration = null;
                previousInstallExisted = false;
                transactionStagingPath = stagingPath;
                Operation = "run";

                Step = "initialize.staging-path";
                ValidateSiblingPath(stagingPath, expectedStagingPrefix, false,
                    "The SIDEY staging directory must be a reserved sibling of the install directory.");
                Step = "initialize.rollback-path";
                ValidateSiblingPath(rollbackPath, installPath + ".sidey-rollback", true,
                    "The SIDEY rollback directory must be the reserved sibling of the install directory.");
            }

            public static bool IsKnownAction(string value)
            {
                return string.Equals(value, "InspectLegacyLocation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "InspectRelocationTarget", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Recover", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Prepare", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Activate", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "BeginRegistration", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Commit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Rollback", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Complete", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "CleanupForUninstall", StringComparison.OrdinalIgnoreCase);
            }

            public int Run()
            {
                if (EqualsAction("InspectLegacyLocation"))
                {
                    Operation = "inspect-legacy.parent-security";
                    Step = "inspect-legacy.parent-security";
                    try
                    {
                        AssertSecureTransactionParent();
                        return 0;
                    }
                    catch (InsecureTransactionParentException)
                    {
                        Operation = "inspect-legacy.transaction-artifacts";
                        Step = "inspect-legacy.transaction-artifacts";
                        if (HasTransactionArtifacts())
                        {
                            throw new InvalidOperationException(
                                "A SIDEY transaction must be recovered at its original location.");
                        }
                        return LegacyRelocationEligible;
                    }
                }
                if (EqualsAction("InspectRelocationTarget"))
                {
                    Operation = "inspect-relocation.parent-security";
                    Step = "inspect-relocation.parent-security";
                    AssertSecureTransactionParent();
                    Operation = "inspect-relocation.target";
                    Step = "inspect-relocation.target";
                    if (Directory.Exists(installPath) || File.Exists(installPath)
                        || HasTransactionArtifacts())
                    {
                        throw new InvalidOperationException(
                            "The SIDEY relocation target is already in use.");
                    }
                    return 0;
                }
                if (EqualsAction("Recover"))
                {
                    Operation = "recover.parent-security";
                    Step = "recover.parent-security";
                    AssertSecureTransactionParent();
                    Operation = "recover.read-state";
                    Step = "recover.read-state";
                    RecoverInterruptedTransaction();
                    Operation = "recover.pending-location";
                    Step = "recover.pending-location";
                    ClearPendingInstallLocation();
                    return 0;
                }
                if (EqualsAction("Prepare"))
                {
                    Operation = "prepare";
                    Step = "prepare.parent-security";
                    AssertSecureTransactionParent();
                    Step = "prepare.recover";
                    RecoverInterruptedTransaction();
                    Operation = "prepare";
                    Step = "prepare.previous-registration";
                    transactionStagingPath = stagingPath;
                    completionId = Guid.NewGuid().ToString("N");
                    previousInstallExisted = Directory.Exists(installPath);
                    previousRegistration = GetPreviousRegistration();
                    Step = "prepare.pending-location";
                    SetPendingInstallLocation();
                    Step = "prepare.write-state";
                    WriteState("staging");
                    Step = "prepare.create-staging";
                    Directory.CreateDirectory(stagingPath);
                    Step = "prepare.protect-staging";
                    ProtectStagingDirectory();
                    return 0;
                }
                if (EqualsAction("Activate"))
                {
                    Operation = "activate";
                    Activate();
                    return 0;
                }
                if (EqualsAction("BeginRegistration"))
                {
                    Step = "begin-registration.read-state";
                    TransactionState state = ReadState();
                    Step = "begin-registration.verify-active";
                    if (state == null || !PhaseEquals(state, "active")
                        || !File.Exists(Path.Combine(installPath, "SIDEY.exe")))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction is not ready to register.");
                    }
                    Step = "begin-registration.write-state";
                    WriteState("registering");
                    return 0;
                }
                if (EqualsAction("Commit"))
                {
                    Step = "commit.read-state";
                    TransactionState state = ReadState();
                    Step = "commit.verify-registration";
                    if (state == null || !PhaseEquals(state, "registering")
                        || !File.Exists(Path.Combine(installPath, "SIDEY.exe")))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction is not ready to commit.");
                    }
                    Step = "commit.write-state";
                    WriteState("committed");
                    return 0;
                }
                if (EqualsAction("Rollback"))
                {
                    Step = "rollback.read-state";
                    TransactionState state = ReadState();
                    if (state != null)
                    {
                        UndoTransaction(state);
                    }
                    else
                    {
                        Step = "rollback.remove-staging";
                        RemoveTransactionDirectory(stagingPath);
                        Step = "rollback.pending-location";
                        ClearPendingInstallLocation();
                    }
                    return 0;
                }
                if (EqualsAction("Complete"))
                {
                    Step = "complete.read-state";
                    TransactionState state = ReadState();
                    Step = "complete.verify-commit";
                    if (state == null || !PhaseEquals(state, "committed"))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction was not committed.");
                    }
                    try
                    {
                        Step = "complete.desktop-registration";
                        CompleteDesktopRegistration(state);
                        Step = "complete.remove-rollback";
                        RemoveTransactionDirectory(rollbackPath);
                        Step = "complete.remove-staging";
                        RemoveTransactionDirectory(AssertStagingDirectoryPath(state.StagingDirectory));
                        Step = "complete.remove-state";
                        RemoveState();
                        Step = "complete.pending-location";
                        ClearPendingInstallLocation();
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        LastFailure = exception;
                        Console.Error.WriteLine(exception.Message);
                        return RetryableCleanupFailure;
                    }
                }

                Step = "uninstall.parent-security";
                AssertSecureTransactionParent();
                CleanupForUninstall();
                return 0;
            }

            private bool EqualsAction(string expected)
            {
                return string.Equals(action, expected, StringComparison.OrdinalIgnoreCase);
            }

            private static bool PhaseEquals(TransactionState state, string expected)
            {
                return string.Equals(state.Phase, expected, StringComparison.Ordinal);
            }

            private void Activate()
            {
                Step = "activate.read-state";
                TransactionState state = ReadState();
                Step = "activate.verify-state";
                if (state == null || !PhaseEquals(state, "staging")
                    || !PathComparer.Equals(Normalize(state.StagingDirectory), stagingPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY staging transaction is not ready to activate.");
                }

                string[] requiredPaths =
                {
                    Path.Combine(stagingPath, "SIDEY.exe"),
                    Path.Combine(stagingPath, @"Runtime\SIDEY.Host.exe"),
                    Path.Combine(stagingPath, @"Runtime\SIDEY.UninstallHelper.exe"),
                    Path.Combine(stagingPath, "Uninstall.exe"),
                };
                string[] requiredRoles = { "launcher", "host", "uninstall-helper", "uninstaller" };
                for (int index = 0; index < requiredPaths.Length; index++)
                {
                    Step = "activate.verify-payload." + requiredRoles[index];
                    if (!File.Exists(requiredPaths[index]))
                    {
                        throw new InvalidOperationException(
                            "The staged SIDEY payload is incomplete: " + requiredPaths[index]);
                    }
                }

                Step = "activate.verify-staging-tree";
                AssertNoReparseTree(stagingPath);
                Step = "activate.verify-install-directory";
                AssertOrdinaryDirectory(installPath);
                Step = "activate.verify-rollback-directory";
                if (Directory.Exists(rollbackPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY rollback directory was not cleared before activation.");
                }

                Step = "activate.write-prepared";
                WriteState("prepared");
                try
                {
                    if (Directory.Exists(installPath))
                    {
                        Step = "activate.move-live";
                        MoveDirectoryForActivation(installPath, rollbackPath);
                        Step = "activate.write-previous-moved";
                        WriteState("previous-moved");
                    }
                    Step = "activate.write-activating";
                    WriteState("activating");
                    Step = "activate.move-staging";
                    MoveDirectoryForActivation(stagingPath, installPath);
                    Step = "activate.write-active";
                    WriteState("active");
                }
                catch (Exception activationFailure)
                {
                    string activationStep = Step;
                    try
                    {
                        Step = "activate.rollback.read-state";
                        TransactionState failedState = ReadState();
                        if (failedState != null)
                        {
                            UndoTransaction(failedState);
                        }
                    }
                    catch
                    {
                        EarlierFailure = activationFailure;
                        EarlierFailureStep = activationStep;
                        throw;
                    }
                    Step = activationStep;
                    throw;
                }
            }

            private static void MoveDirectoryForActivation(string source, string destination)
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        Directory.Move(source, destination);
                        return;
                    }
                    catch (Exception exception) when (
                        IsRetryableMoveFailure(exception)
                        && attempt < ActivationMoveAttempts)
                    {
                        Thread.Sleep(ActivationMoveRetryDelayMilliseconds);
                    }
                }
            }

            private static bool IsRetryableMoveFailure(Exception exception)
            {
                return exception is IOException || exception is UnauthorizedAccessException;
            }

            private void CleanupForUninstall()
            {
                Step = "uninstall.read-state";
                TransactionState state = ReadState();
                if (state != null)
                {
                    if (PhaseEquals(state, "committed"))
                    {
                        Step = "uninstall.remove-rollback";
                        RemoveTransactionDirectory(rollbackPath);
                        Step = "uninstall.remove-staging";
                        RemoveTransactionDirectory(AssertStagingDirectoryPath(state.StagingDirectory));
                        Step = "uninstall.remove-state";
                        RemoveState();
                        Step = "uninstall.pending-location";
                        ClearPendingInstallLocation();
                    }
                    else
                    {
                        UndoTransaction(state);
                    }
                }
                else
                {
                    Step = "uninstall.remove-rollback";
                    RemoveTransactionDirectory(rollbackPath);
                    Step = "uninstall.remove-staging";
                    RemoveTransactionDirectory(stagingPath);
                    Step = "uninstall.pending-location";
                    ClearPendingInstallLocation();
                }
            }

            private void RecoverInterruptedTransaction()
            {
                TransactionState state = ReadState();
                if (state == null)
                {
                    Operation = "recover.inspect-orphan";
                    if (Directory.Exists(rollbackPath))
                    {
                        throw new InvalidOperationException(
                            "An unrecognized SIDEY rollback directory already exists.");
                    }
                    Operation = "recover.remove-staging";
                    Step = action.ToLowerInvariant() + ".recover.remove-staging";
                    RemoveTransactionDirectory(stagingPath);
                    return;
                }

                if (PhaseEquals(state, "committed"))
                {
                    Operation = "recover.complete-committed";
                    Step = action.ToLowerInvariant() + ".recover.validate-staging";
                    string recordedStaging = AssertStagingDirectoryPath(state.StagingDirectory);
                    if (!Directory.Exists(installPath))
                    {
                        UndoTransaction(state);
                        return;
                    }
                    CompleteDesktopRegistration(state);
                    Step = action.ToLowerInvariant() + ".recover.remove-rollback";
                    RemoveTransactionDirectory(rollbackPath);
                    Step = action.ToLowerInvariant() + ".recover.remove-staging";
                    RemoveTransactionDirectory(recordedStaging);
                    Step = action.ToLowerInvariant() + ".recover.remove-state";
                    RemoveState();
                    return;
                }
                Operation = "recover.rollback";
                UndoTransaction(state);
            }

            private void CompleteDesktopRegistration(TransactionState state)
            {
                if (allowUserWritableParentForTests || string.IsNullOrWhiteSpace(state.CompletionId))
                {
                    // Older transaction schemas did not request desktop completion.
                    return;
                }
                // This phase is retried on committed recovery, before discarding
                // transaction state. Never roll back a committed installation.
                bool fresh = !state.PreviousInstallExisted
                    && (state.PreviousRegistration == null || !state.PreviousRegistration.Existed);
                Version prior;
                Version current;
                bool upgrade = state.PreviousRegistration != null
                    && state.PreviousRegistration.Existed
                    && Version.TryParse(state.PreviousRegistration.UpdateVersion, out prior)
                    && Version.TryParse(state.UpdateVersion, out current)
                    && current > prior;
                bool relocated = upgrade && !state.PreviousInstallExisted;
                string markerPath = Path.Combine(installPath, "install-completion.txt");
                Step = action.ToLowerInvariant() + ".desktop-registration.write-marker";
                File.WriteAllLines(markerPath, new[]
                {
                    fresh ? "fresh" : (relocated ? "relocate" : (upgrade ? "upgrade" : "repair")),
                    state.UpdateVersion,
                    state.CompletionId,
                });
                Step = action.ToLowerInvariant() + ".desktop-registration.start-helper";
                using (Process helper = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(installPath, @"Runtime\SIDEY.UninstallHelper.exe"),
                    Arguments = "--complete-install-as-desktop-user",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }))
                {
                    if (helper == null)
                    {
                        throw new InvalidOperationException("Desktop registration did not start.");
                    }
                    Step = action.ToLowerInvariant() + ".desktop-registration.wait-helper";
                    helper.WaitForExit();
                    Step = action.ToLowerInvariant() + ".desktop-registration.verify-helper";
                    if (helper.ExitCode != 0)
                    {
                        throw new InvalidOperationException("Desktop registration is pending recovery.");
                    }
                }
                Step = action.ToLowerInvariant() + ".desktop-registration.remove-marker";
                File.Delete(markerPath);
            }

            private void UndoTransaction(TransactionState state)
            {
                string prefix = action.ToLowerInvariant() + ".undo";
                Step = prefix + ".validate-staging";
                string stagingToRemove = AssertStagingDirectoryPath(state.StagingDirectory);
                string startingPhase = state.Phase;
                if (string.Equals(startingPhase, "rolled-back", StringComparison.Ordinal))
                {
                    Step = prefix + ".remove-rollback";
                    RemoveTransactionDirectory(rollbackPath);
                    Step = prefix + ".remove-staging";
                    RemoveTransactionDirectory(stagingToRemove);
                    if (!PathComparer.Equals(stagingToRemove, stagingPath))
                    {
                        RemoveTransactionDirectory(stagingPath);
                    }
                    Step = prefix + ".remove-state";
                    RemoveState();
                    Step = prefix + ".pending-location";
                    ClearPendingInstallLocation();
                    return;
                }

                if (!string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    Step = prefix + ".write-rolling-back";
                    WriteState("rolling-back");
                }
                Step = prefix + ".verify-rollback";
                AssertOrdinaryDirectory(rollbackPath);
                if (Directory.Exists(rollbackPath))
                {
                    if (Directory.Exists(installPath))
                    {
                        Step = prefix + ".remove-live";
                        RemoveTransactionDirectory(installPath);
                    }
                    Step = prefix + ".restore-live";
                    Directory.Move(rollbackPath, installPath);
                }
                else if (previousInstallExisted
                    && !string.Equals(startingPhase, "staging", StringComparison.Ordinal)
                    && !string.Equals(startingPhase, "prepared", StringComparison.Ordinal)
                    && !string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The SIDEY rollback directory is missing for the previous installation.");
                }
                else if (!previousInstallExisted
                    && (string.Equals(startingPhase, "activating", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "active", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "registering", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "committed", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal)))
                {
                    Step = prefix + ".remove-live";
                    RemoveTransactionDirectory(installPath);
                }

                Step = prefix + ".remove-staging";
                RemoveTransactionDirectory(stagingToRemove);
                if (!PathComparer.Equals(stagingToRemove, stagingPath))
                {
                    RemoveTransactionDirectory(stagingPath);
                }
                if (string.Equals(startingPhase, "registering", StringComparison.Ordinal)
                    || string.Equals(startingPhase, "committed", StringComparison.Ordinal)
                    || string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    Step = prefix + ".restore-registration";
                    RestorePreviousRegistration();
                }
                Step = prefix + ".write-rolled-back";
                WriteState("rolled-back");
                Step = prefix + ".remove-state";
                RemoveState();
                Step = prefix + ".pending-location";
                ClearPendingInstallLocation();
            }

            private void ValidateSiblingPath(
                string path,
                string expected,
                bool exact,
                string message)
            {
                DirectoryInfo parent = Directory.GetParent(path);
                bool nameMatches = exact
                    ? PathComparer.Equals(path, expected)
                    : path.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
                if (!nameMatches || parent == null
                    || !PathComparer.Equals(Normalize(parent.FullName), parentPath))
                {
                    throw new InvalidOperationException(message);
                }
            }

            private string AssertStagingDirectoryPath(string path)
            {
                string normalized = Normalize(path);
                ValidateSiblingPath(
                    normalized,
                    expectedStagingPrefix,
                    false,
                    "The SIDEY transaction state contains an unsafe staging directory.");
                return normalized;
            }

            private static string Normalize(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("A SIDEY transaction path is empty.");
                }
                return Path.GetFullPath(path).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            private static void AssertOrdinaryDirectory(string path)
            {
                if (Directory.Exists(path)
                    && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing to use a reparse point for the SIDEY install transaction: " + path);
                }
            }

            private static void AssertNoReparseAncestors(string path)
            {
                for (DirectoryInfo current = new DirectoryInfo(path);
                    current != null;
                    current = current.Parent)
                {
                    if (current.Exists
                        && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "Refusing a reparse-point ancestor for the SIDEY install transaction: "
                            + current.FullName);
                    }
                }
            }

            private static void AssertNoReparseTree(string path)
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(path));
                while (pending.Count > 0)
                {
                    DirectoryInfo directory = pending.Pop();
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "Refusing a reparse point in the SIDEY install transaction: "
                            + directory.FullName);
                    }
                    foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
                    {
                        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidOperationException(
                                "Refusing a reparse point in the SIDEY install transaction: "
                                + item.FullName);
                        }
                        DirectoryInfo child = item as DirectoryInfo;
                        if (child != null)
                        {
                            pending.Push(child);
                        }
                    }
                }
            }

            private void AssertSecureTransactionParent()
            {
                string stepPrefix = Step;
                Step = stepPrefix + ".reparse";
                AssertNoReparseAncestors(parentPath);
                if (allowUserWritableParentForTests)
                {
                    return;
                }

                const string administrators = "S-1-5-32-544";
                const string system = "S-1-5-18";
                const string trustedInstaller =
                    "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
                const string creatorOwner = "S-1-3-0";
                var privileged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    administrators,
                    system,
                    trustedInstaller,
                };

                Step = stepPrefix + ".read-acl";
                DirectorySecurity security = Directory.GetAccessControl(
                    parentPath,
                    AccessControlSections.Access | AccessControlSections.Owner);
                Step = stepPrefix + ".owner";
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner == null || !privileged.Contains(owner.Value))
                {
                    throw new InsecureTransactionParentException(
                        "The SIDEY install directory parent must be owned by Administrators, SYSTEM, or TrustedInstaller.");
                }

                const FileSystemRights writeRights = FileSystemRights.Write
                    | FileSystemRights.Delete
                    | FileSystemRights.DeleteSubdirectoriesAndFiles
                    | FileSystemRights.ChangePermissions
                    | FileSystemRights.TakeOwnership;
                Step = stepPrefix + ".write-permissions";
                AuthorizationRuleCollection rules = security.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    uint rawRights = unchecked((uint)rule.FileSystemRights);
                    bool hasGenericWrite = (rawRights & 0x50000000U) != 0;
                    string identity = rule.IdentityReference.Value;
                    bool safeCreatorOwnerInheritance = string.Equals(
                            identity,
                            creatorOwner,
                            StringComparison.OrdinalIgnoreCase)
                        && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
                    if (rule.AccessControlType == AccessControlType.Allow
                        && !safeCreatorOwnerInheritance
                        && (((rule.FileSystemRights & writeRights) != 0) || hasGenericWrite)
                        && !privileged.Contains(identity))
                    {
                        throw new InsecureTransactionParentException(
                            "The SIDEY install directory parent is writable by an unprivileged identity: "
                            + identity);
                    }
                }
            }

            private bool HasTransactionArtifacts()
            {
                if (File.Exists(statePath) || Directory.Exists(statePath)
                    || File.Exists(statePath + ".tmp") || Directory.Exists(statePath + ".tmp")
                    || File.Exists(rollbackPath) || Directory.Exists(rollbackPath))
                {
                    return true;
                }

                string stagingPattern = Path.GetFileName(installPath) + ".sidey-staging-*";
                foreach (string entry in Directory.EnumerateFileSystemEntries(parentPath, stagingPattern))
                {
                    return true;
                }
                return false;
            }

            private void ProtectStagingDirectory()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                var administrators = new SecurityIdentifier("S-1-5-32-544");
                var system = new SecurityIdentifier("S-1-5-18");
                var users = new SecurityIdentifier("S-1-5-32-545");
                const InheritanceFlags inheritance =
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.SetOwner(administrators);
                security.AddAccessRule(new FileSystemAccessRule(
                    administrators,
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    system,
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.ReadAndExecute,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                Directory.SetAccessControl(stagingPath, security);
            }

            private void RemoveTransactionDirectory(string path)
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
                AssertNoReparseTree(path);
                RemoveDirectoryTree(new DirectoryInfo(path));
            }

            private static void RemoveDirectoryTree(DirectoryInfo directory)
            {
                directory.Refresh();
                if (!directory.Exists)
                {
                    return;
                }
                AssertNotReparsePoint(directory);
                foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
                {
                    item.Refresh();
                    AssertNotReparsePoint(item);
                    DirectoryInfo childDirectory = item as DirectoryInfo;
                    if (childDirectory != null)
                    {
                        RemoveDirectoryTree(childDirectory);
                        continue;
                    }

                    NormalizeDeletionAttributes(item);
                    File.Delete(item.FullName);
                }

                // Recheck immediately before deletion. Directory.Delete(false)
                // never follows a newly substituted directory tree recursively.
                directory.Refresh();
                AssertNotReparsePoint(directory);
                NormalizeDeletionAttributes(directory);
                Directory.Delete(directory.FullName, false);
            }

            private static void AssertNotReparsePoint(FileSystemInfo item)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing a reparse point in the SIDEY install transaction: "
                        + item.FullName);
                }
            }

            private static void NormalizeDeletionAttributes(FileSystemInfo item)
            {
                FileAttributes attributes = item.Attributes;
                FileAttributes normalized = attributes
                    & ~FileAttributes.ReadOnly
                    & ~FileAttributes.Hidden
                    & ~FileAttributes.System;
                if (normalized != attributes)
                {
                    File.SetAttributes(item.FullName, normalized);
                }
            }

            private void SetPendingInstallLocation()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                using (RegistryKey key = machine.CreateSubKey(TransactionRegistryPath))
                {
                    key.SetValue("InstallLocation", installPath, RegistryValueKind.String);
                }
            }

            private void ClearPendingInstallLocation()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                {
                    machine.DeleteSubKeyTree(TransactionRegistryPath, false);
                }
            }

            private PreviousRegistration GetPreviousRegistration()
            {
                if (allowUserWritableParentForTests)
                {
                    return new PreviousRegistration { Managed = false };
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                using (RegistryKey installer = machine.OpenSubKey(InstallerRegistryPath))
                using (RegistryKey uninstall = machine.OpenSubKey(UninstallRegistryPath))
                {
                    object previousUpdateVersion = installer == null
                        ? null
                        : installer.GetValue("InstalledVersion", null);
                    string updateVersion = Convert.ToString(
                        previousUpdateVersion,
                        CultureInfo.InvariantCulture);
                    string displayVersion = uninstall == null
                        ? updateVersion
                        : Convert.ToString(
                            uninstall.GetValue("DisplayVersion", updateVersion),
                            CultureInfo.InvariantCulture);
                    return new PreviousRegistration
                    {
                        Managed = true,
                        Existed = previousUpdateVersion != null,
                        ProductVersion = displayVersion,
                        UpdateVersion = updateVersion,
                        Language = installer == null
                            ? string.Empty
                            : Convert.ToString(installer.GetValue("Language", string.Empty), CultureInfo.InvariantCulture),
                        Location = installer == null
                            ? installPath
                            : Convert.ToString(installer.GetValue("InstallLocation", installPath), CultureInfo.InvariantCulture),
                    };
                }
            }

            private void RestorePreviousRegistration()
            {
                if (previousRegistration == null || !previousRegistration.Managed)
                {
                    return;
                }

                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                {
                    Step = action.ToLowerInvariant() + ".restore-registration.remove-keys";
                    machine.DeleteSubKeyTree(InstallerRegistryPath, false);
                    machine.DeleteSubKeyTree(UninstallRegistryPath, false);
                    machine.DeleteSubKeyTree(ProtocolRegistryPath, false);

                    Step = action.ToLowerInvariant() + ".restore-registration.shortcuts";
                    string startMenu = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                        "SIDEY");
                    // The folder can contain files we do not own. Only remove the
                    // shortcuts that this installer creates before restoring them.
                    AssertOrdinaryDirectory(startMenu);
                    File.Delete(Path.Combine(startMenu, "SIDEY.lnk"));
                    File.Delete(Path.Combine(startMenu, "Uninstall SIDEY.lnk"));
                    if (!previousRegistration.Existed)
                    {
                        if (Directory.Exists(startMenu)
                            && Directory.GetFileSystemEntries(startMenu).Length == 0)
                        {
                            Directory.Delete(startMenu, false);
                        }
                        return;
                    }

                    string location = previousRegistration.Location;
                    string priorProductVersion = previousRegistration.ProductVersion;
                    string priorUpdateVersion = previousRegistration.UpdateVersion;
                    Step = action.ToLowerInvariant() + ".restore-registration.write-keys";
                    using (RegistryKey installer = machine.CreateSubKey(InstallerRegistryPath))
                    using (RegistryKey uninstall = machine.CreateSubKey(UninstallRegistryPath))
                    using (RegistryKey protocol = machine.CreateSubKey(ProtocolRegistryPath))
                    {
                        if (!string.IsNullOrWhiteSpace(previousRegistration.Language))
                        {
                            installer.SetValue(
                                "Language",
                                previousRegistration.Language,
                                RegistryValueKind.String);
                        }
                        installer.SetValue("InstallLocation", location, RegistryValueKind.String);
                        installer.SetValue("InstalledVersion", priorUpdateVersion, RegistryValueKind.String);
                        uninstall.SetValue("DisplayName", "SIDEY", RegistryValueKind.String);
                        uninstall.SetValue("Publisher", "SIDEY", RegistryValueKind.String);
                        uninstall.SetValue("InstallLocation", location, RegistryValueKind.String);
                        uninstall.SetValue(
                            "DisplayIcon",
                            Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"),
                            RegistryValueKind.String);
                        uninstall.SetValue(
                            "UninstallString",
                            Quote(Path.Combine(location, "Uninstall.exe")),
                            RegistryValueKind.String);
                        uninstall.SetValue(
                            "QuietUninstallString",
                            Quote(Path.Combine(location, "Uninstall.exe")) + " /S",
                            RegistryValueKind.String);
                        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        uninstall.SetValue("DisplayVersion", priorProductVersion, RegistryValueKind.String);
                        protocol.SetValue(
                            string.Empty,
                            "URL:SIDEY authentication callback",
                            RegistryValueKind.String);
                        protocol.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
                        using (RegistryKey icon = protocol.CreateSubKey("DefaultIcon"))
                        using (RegistryKey command = protocol.CreateSubKey(@"shell\open\command"))
                        {
                            icon.SetValue(
                                string.Empty,
                                Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"),
                                RegistryValueKind.String);
                            command.SetValue(
                                string.Empty,
                                Quote(Path.Combine(location, "SIDEY.exe")) + " \"%1\"",
                                RegistryValueKind.String);
                        }
                    }

                    Step = action.ToLowerInvariant() + ".restore-registration.create-shortcuts";
                    Directory.CreateDirectory(startMenu);
                    CreateShortcut(
                        Path.Combine(startMenu, "SIDEY.lnk"),
                        Path.Combine(location, "SIDEY.exe"),
                        Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"));
                    CreateShortcut(
                        Path.Combine(startMenu, "Uninstall SIDEY.lnk"),
                        Path.Combine(location, "Uninstall.exe"),
                        Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"));
                }
            }

            private static string Quote(string value)
            {
                return "\"" + value + "\"";
            }

            private static void CreateShortcut(string path, string target, string icon)
            {
                var shellLink = (IShellLinkW)new ShellLink();
                try
                {
                    shellLink.SetPath(target);
                    shellLink.SetIconLocation(icon, 0);
                    ((IPersistFile)shellLink).Save(path, true);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shellLink);
                }
            }

            private void WriteState(string phase)
            {
                string temporaryPath = statePath + ".tmp";
                AssertOrdinaryStateFile(statePath);
                AssertOrdinaryStateFile(temporaryPath);
                var state = new TransactionState
                {
                    SchemaVersion = 2,
                    Phase = phase,
                    CompletionId = completionId,
                    ProductVersion = productVersion,
                    UpdateVersion = updateVersion,
                    InstallDirectory = installPath,
                    StagingDirectory = transactionStagingPath,
                    RollbackDirectory = rollbackPath,
                    PreviousInstallExisted = previousInstallExisted,
                    PreviousRegistration = previousRegistration,
                };
                File.WriteAllText(temporaryPath, StateJson.Serialize(state), new UTF8Encoding(false));
                if (!MoveFileEx(
                    temporaryPath,
                    statePath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not publish SIDEY transaction state.");
                }
            }

            private TransactionState ReadState()
            {
                if (!File.Exists(statePath))
                {
                    return null;
                }
                AssertOrdinaryStateFile(statePath);
                TransactionState state = StateJson.Deserialize(
                    File.ReadAllText(statePath, Encoding.UTF8));
                if ((state.SchemaVersion != 1 && state.SchemaVersion != 2)
                    || !PathComparer.Equals(Normalize(state.InstallDirectory), installPath)
                    || !PathComparer.Equals(Normalize(state.RollbackDirectory), rollbackPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY install transaction state is invalid.");
                }
                transactionStagingPath = AssertStagingDirectoryPath(state.StagingDirectory);
                completionId = state.CompletionId;
                previousRegistration = state.PreviousRegistration;
                previousInstallExisted = state.PreviousInstallExisted;
                return state;
            }

            private void RemoveState()
            {
                foreach (string path in new[] { statePath, statePath + ".tmp" })
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    AssertOrdinaryStateFile(path);
                    File.Delete(path);
                }
            }

            private static void AssertOrdinaryStateFile(string path)
            {
                if (File.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing a reparse point for SIDEY transaction state.");
                }
            }

            private const uint MoveFileReplaceExisting = 0x1;
            private const uint MoveFileWriteThrough = 0x8;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool MoveFileEx(
                string existingFile,
                string newFile,
                uint flags);
        }

        private static class StateJson
        {
            public static string Serialize(TransactionState state)
            {
                var builder = new StringBuilder();
                builder.Append('{');
                Property(builder, "schemaVersion", state.SchemaVersion.ToString(CultureInfo.InvariantCulture), false);
                Property(builder, "phase", String(state.Phase), true);
                Property(builder, "completionId", String(state.CompletionId), true);
                Property(builder, "productVersion", String(state.ProductVersion), true);
                Property(builder, "updateVersion", String(state.UpdateVersion), true);
                Property(builder, "installDirectory", String(state.InstallDirectory), true);
                Property(builder, "stagingDirectory", String(state.StagingDirectory), true);
                Property(builder, "rollbackDirectory", String(state.RollbackDirectory), true);
                Property(builder, "previousInstallExisted", state.PreviousInstallExisted ? "true" : "false", true);
                builder.Append(',').Append(String("previousRegistration")).Append(':');
                if (state.PreviousRegistration == null)
                {
                    builder.Append("null");
                }
                else
                {
                    PreviousRegistration registration = state.PreviousRegistration;
                    builder.Append('{');
                    Property(builder, "managed", registration.Managed ? "true" : "false", false);
                    Property(builder, "existed", registration.Existed ? "true" : "false", true);
                    Property(builder, "productVersion", String(registration.ProductVersion), true);
                    Property(builder, "updateVersion", String(registration.UpdateVersion), true);
                    Property(builder, "language", String(registration.Language), true);
                    Property(builder, "location", String(registration.Location), true);
                    builder.Append('}');
                }
                return builder.Append('}').ToString();
            }

            public static TransactionState Deserialize(string json)
            {
                object parsed = new JsonParser(json).Parse();
                IDictionary<string, object> root = parsed as IDictionary<string, object>;
                if (root == null)
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                int schemaVersion = Integer(root, "schemaVersion");
                string legacyVersion = schemaVersion == 1 ? Text(root, "version") : null;
                var state = new TransactionState
                {
                    SchemaVersion = schemaVersion,
                    Phase = Text(root, "phase"),
                    CompletionId = OptionalText(root, "completionId"),
                    ProductVersion = schemaVersion == 1
                        ? legacyVersion
                        : Text(root, "productVersion"),
                    UpdateVersion = schemaVersion == 1
                        ? legacyVersion
                        : Text(root, "updateVersion"),
                    InstallDirectory = Text(root, "installDirectory"),
                    StagingDirectory = Text(root, "stagingDirectory"),
                    RollbackDirectory = Text(root, "rollbackDirectory"),
                    PreviousInstallExisted = Boolean(root, "previousInstallExisted", false),
                };
                object registrationValue;
                if (root.TryGetValue("previousRegistration", out registrationValue)
                    && registrationValue != null)
                {
                    IDictionary<string, object> registration =
                        registrationValue as IDictionary<string, object>;
                    if (registration == null)
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction state is invalid.");
                    }
                    string legacyRegistrationVersion = schemaVersion == 1
                        ? OptionalText(registration, "version")
                        : null;
                    state.PreviousRegistration = new PreviousRegistration
                    {
                        Managed = Boolean(registration, "managed", false),
                        Existed = Boolean(registration, "existed", false),
                        ProductVersion = schemaVersion == 1
                            ? legacyRegistrationVersion
                            : OptionalText(registration, "productVersion"),
                        UpdateVersion = schemaVersion == 1
                            ? legacyRegistrationVersion
                            : OptionalText(registration, "updateVersion"),
                        Language = OptionalText(registration, "language"),
                        Location = OptionalText(registration, "location"),
                    };
                }
                return state;
            }

            private static void Property(
                StringBuilder builder,
                string name,
                string value,
                bool comma)
            {
                if (comma)
                {
                    builder.Append(',');
                }
                builder.Append(String(name)).Append(':').Append(value);
            }

            private static string String(string value)
            {
                if (value == null)
                {
                    return "null";
                }
                var builder = new StringBuilder(value.Length + 2).Append('"');
                foreach (char character in value)
                {
                    switch (character)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (character < 0x20)
                            {
                                builder.Append("\\u")
                                    .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(character);
                            }
                            break;
                    }
                }
                return builder.Append('"').ToString();
            }

            private static int Integer(IDictionary<string, object> values, string key)
            {
                object value;
                if (!values.TryGetValue(key, out value) || !(value is long))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return checked((int)(long)value);
            }

            private static string Text(IDictionary<string, object> values, string key)
            {
                object value;
                string text;
                if (!values.TryGetValue(key, out value)
                    || (text = value as string) == null
                    || string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return text;
            }

            private static string OptionalText(IDictionary<string, object> values, string key)
            {
                object value;
                if (!values.TryGetValue(key, out value) || value == null)
                {
                    return string.Empty;
                }
                string text = value as string;
                if (text == null)
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return text;
            }

            private static bool Boolean(
                IDictionary<string, object> values,
                string key,
                bool defaultValue)
            {
                object value;
                if (!values.TryGetValue(key, out value))
                {
                    return defaultValue;
                }
                if (!(value is bool))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return (bool)value;
            }
        }

        private sealed class JsonParser
        {
            private readonly string text;
            private int index;

            public JsonParser(string textValue)
            {
                text = textValue ?? string.Empty;
            }

            public object Parse()
            {
                object value = Value();
                WhiteSpace();
                if (index != text.Length)
                {
                    Invalid();
                }
                return value;
            }

            private object Value()
            {
                WhiteSpace();
                if (index >= text.Length)
                {
                    Invalid();
                }
                char character = text[index];
                if (character == '{') return Object();
                if (character == '[') return Array();
                if (character == '"') return String();
                if (character == '-' || char.IsDigit(character)) return Number();
                if (Literal("true")) return true;
                if (Literal("false")) return false;
                if (Literal("null")) return null;
                Invalid();
                return null;
            }

            private IDictionary<string, object> Object()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                index++;
                WhiteSpace();
                if (Take('}')) return result;
                while (true)
                {
                    WhiteSpace();
                    if (index >= text.Length || text[index] != '"') Invalid();
                    string key = String();
                    WhiteSpace();
                    if (!Take(':')) Invalid();
                    if (result.ContainsKey(key)) Invalid();
                    result.Add(key, Value());
                    WhiteSpace();
                    if (Take('}')) return result;
                    if (!Take(',')) Invalid();
                }
            }

            private IList Array()
            {
                var result = new ArrayList();
                index++;
                WhiteSpace();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(Value());
                    WhiteSpace();
                    if (Take(']')) return result;
                    if (!Take(',')) Invalid();
                }
            }

            private string String()
            {
                index++;
                var result = new StringBuilder();
                while (index < text.Length)
                {
                    char character = text[index++];
                    if (character == '"') return result.ToString();
                    if (character != '\\')
                    {
                        if (character < 0x20) Invalid();
                        result.Append(character);
                        continue;
                    }
                    if (index >= text.Length) Invalid();
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length) Invalid();
                            int code;
                            if (!int.TryParse(
                                text.Substring(index, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out code)) Invalid();
                            result.Append((char)code);
                            index += 4;
                            break;
                        default: Invalid(); break;
                    }
                }
                Invalid();
                return null;
            }

            private long Number()
            {
                int start = index;
                if (text[index] == '-') index++;
                if (index >= text.Length || !char.IsDigit(text[index])) Invalid();
                while (index < text.Length && char.IsDigit(text[index])) index++;
                long value;
                if (!long.TryParse(
                    text.Substring(start, index - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)) Invalid();
                return value;
            }

            private bool Literal(string value)
            {
                if (index + value.Length > text.Length
                    || !string.Equals(
                        text.Substring(index, value.Length),
                        value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                index += value.Length;
                return true;
            }

            private bool Take(char character)
            {
                if (index < text.Length && text[index] == character)
                {
                    index++;
                    return true;
                }
                return false;
            }

            private void WhiteSpace()
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            }

            private static void Invalid()
            {
                throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath,
                IntPtr findData, uint flags);
            void GetIDList(out IntPtr itemIdentifierList);
            void SetIDList(IntPtr itemIdentifierList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
                int maximumPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
                int iconPathLength,
                out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
