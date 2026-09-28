namespace AbyssRunner.UI;
public sealed class CalibrationForm : Form
{
    public CalibrationForm(AbyssRunner.Config.LoadedConfig config, nint hwnd)
    {
        Text = "템플릿 캡처/교정";
        Width = 640;
        Height = 240;
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "이 빌드에서는 images 폴더에 실제 PNG 템플릿을 직접 넣어주세요." });
    }
}
