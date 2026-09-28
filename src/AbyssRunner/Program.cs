using AbyssRunner.Config;
using AbyssRunner.UI;

namespace AbyssRunner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var baseDir = AppContext.BaseDirectory;
        var config = ConfigStore.LoadOrCreate(baseDir);
        Application.Run(new MainForm(config, baseDir));
    }
}
