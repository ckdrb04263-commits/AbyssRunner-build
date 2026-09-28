using AbyssRunner.Config;
using AbyssRunner.Core;
using AbyssRunner.Vision;

namespace AbyssRunner.UI;

public sealed class RecognitionTestForm : Form
{
    private readonly LoadedConfig _config;
    private readonly nint _hwnd;
    private readonly RichTextBox _output = new() { Dock = DockStyle.Fill, ReadOnly = true };
    public RecognitionTestForm(LoadedConfig config, nint hwnd)
    {
        _config = config;
        _hwnd = hwnd;
        Text = "입력 없는 인식 테스트";
        Width = 900;
        Height = 650;
        var button = new Button { Dock = DockStyle.Top, Height = 40, Text = "현재 화면 인식 검사 (입력 없음)" };
        button.Click += async (_, _) => await InspectAsync();
        Controls.Add(_output);
        Controls.Add(button);
    }

    private async Task InspectAsync()
    {
        try
        {
            using var shot = new WindowCapture().Capture(_hwnd);
            var profile = ProfileResolver.Resolve(_config, shot.Width, shot.Height);
            var detector = new Detector(new KoreanOcr(), new TemplateMatcher(Path.Combine(_config.BaseDirectory, "images")));
            var names = new[] { "abyssMenu", "destinationHusang", "destinationKwanggi", "destinationMoolgil", "enter", "resultTouch", "retry", "skipDialogue" };
            _output.Clear();
            foreach (var name in names)
            {
                var d = await detector.DetectAsync(shot, profile, name, CancellationToken.None);
                _output.AppendText($"{name}: matched={d.Matched}, score={d.Score}, ocr={d.ActualOcr}, template={d.TemplateName}, templateScore={d.TemplateScore:0.000}, color={d.ColorEvidence}, reason={d.Reason}\r\n");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "인식 검사 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
