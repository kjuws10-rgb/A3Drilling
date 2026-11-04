using Drilling.Common.Log;
using System.IO;
using System.Text;
using Drilling.Common.Managers;
using Drilling.File.JHMI;
using Drilling.File.Product;
using Drilling.File.ReviewResult;
using Drilling.File.ReviewOffset;
using Drilling.File.Script;

namespace Drilling.UI;
public static class CAppStartup
{
    public static CRootView CreateMainViewModel()
    {
        var configRoot = GetConfigRoot();
        var settingFile = new CSettingFile(configRoot);
        var automationScriptFile = new CAutomation1ScriptFile(GetScriptDirectory(configRoot, settingFile));
        var manager = new CManager(
            configRoot, 
            new CJhmiRecipeFile(configRoot), 
            settingFile, 
            new CManualScanFile(configRoot), 
            new CInterfaceFile(configRoot), 
            new CBETFile(configRoot), 
            new CMotorFile(configRoot), 
            new CMelsecMapFile(configRoot), 
            new CProductFile(configRoot), 
            new CReviewResultFile(configRoot), 
            new CReviewOffsetFile(configRoot), 
            new CLogManager(configRoot), 
            automationScriptFile, 
            configStructureFile: new CConfigStructureFile(configRoot), 
            alarmListFile: new CAlarmListFile(configRoot)
        );
        CUiLogService.Configure(manager.Log());
        var lastLoggedStartupOrder = WriteManagerStartupStatus(manager, "MANAGER_STARTUP_SEQUENCE", 0);
        async global::System.Threading.Tasks.Task SelectValue()
        {
            try
            {
                await manager.Initialize();
                WriteManagerStartupStatus(manager, "MANAGER_INITIALIZE_SEQUENCE", lastLoggedStartupOrder);
            }
            catch (Exception exception)
            {
                CProgramOpenLog.Write("MANAGER_INITIALIZE_FAILED", exception);
                WriteManagerStartupStatus(manager, "MANAGER_INITIALIZE_SEQUENCE", lastLoggedStartupOrder);
            }
        }

        _ = Task.Run(SelectValue);
        var rootView = new CRootView(
            manager, 
            manager.Station(), 
            manager.Interface(), 
            manager.Motion(), 
            manager.Alarm(), 
            manager.InterLock(), 
            manager.ManualScanFile(), 
            manager.Recipe(), 
            manager.Setting(), 
            manager.Product(), 
            manager.Review(), 
            automationScriptFile
        );
        manager.StartBackgroundMonitoring();
        return rootView;
    }

    private static int WriteManagerStartupStatus(CManager manager, string title, int afterOrder)
    {
        var status = manager.ConfigStatus();
        bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_MANAGER_STARTUP_STEP step)
        {
            return step.Order > afterOrder;
        }

        int SelectOrder(global::Drilling.Common.Managers.ST_MANAGER_STARTUP_STEP step)
        {
            return step.Order;
        }

        var steps = status.StartupSteps.Where(ShouldIncludeItem).OrderBy(SelectOrder).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine($"ConfigRoot={status.ConfigRoot}");
        builder.AppendLine($"InterfaceCount={status.InterfaceCount}");
        builder.AppendLine($"MotorCount={status.MotorCount}");
        builder.AppendLine($"MelsecMapCount={status.MelsecMapCount}");
        builder.AppendLine($"ActiveProductLoaded={status.ActiveProductLoaded}");
        if (steps.Length == 0)
        {
            builder.AppendLine("No additional manager startup step.");
        }
        else
        {
            foreach (var step in steps)
            {
                builder.AppendLine($"{step.Order:00} | {step.Result} | {step.StepName} | {step.Message}");
            }
        }

        if (status.StartupMessages.Count > 0)
        {
            builder.AppendLine("StartupMessages:");
            foreach (var message in status.StartupMessages)
            {
                builder.AppendLine($"- {message}");
            }
        }

        CProgramOpenLog.Write(title, builder.ToString().TrimEnd());
        int SelectOrder2(global::Drilling.Common.Managers.ST_MANAGER_STARTUP_STEP step)
        {
            return step.Order;
        }

        return status.StartupSteps.Count == 0 ? afterOrder : status.StartupSteps.Max(SelectOrder2);
    }

    private static string GetConfigRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "Drilling.sln")))
            {
                return Path.Combine(directory.FullName, "Config");
            }

            directory = directory.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "Config");
    }

    private static string GetScriptDirectory(string configRoot, CSettingFile settingFile)
    {
        var projectRoot = Directory.GetParent(configRoot)?.FullName ?? configRoot;
        bool MatchesItem(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER parameter)
        {
            return parameter.Key.Equals("LocalScriptPath", StringComparison.OrdinalIgnoreCase) || parameter.Name.Equals("LocalScriptPath", StringComparison.OrdinalIgnoreCase);
        }

        var scriptPath = settingFile.Load(EN_SETTING_TAB.Option).FirstOrDefault(MatchesItem)?.Value;
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            scriptPath = Path.Combine("Data", "Script");
        }

        return Path.IsPathRooted(scriptPath) ? Path.GetFullPath(scriptPath) : Path.GetFullPath(Path.Combine(projectRoot, scriptPath));
    }
}
