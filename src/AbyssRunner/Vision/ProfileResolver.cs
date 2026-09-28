using AbyssRunner.Config;

namespace AbyssRunner.Vision;

public static class ProfileResolver
{
    public static CaptureProfile Resolve(LoadedConfig config, int width, int height)
    {
        if (config.Targets.Profiles.TryGetValue(config.App.ActiveProfile, out var active) &&
            active.CaptureWidth == width && active.CaptureHeight == height)
            return active;

        var exact = config.Targets.Profiles.Values.FirstOrDefault(p => p.CaptureWidth == width && p.CaptureHeight == height);
        if (exact is not null) return exact;

        throw new InvalidOperationException(
            $"캡처 크기 {width}×{height}에 맞는 프로필이 없습니다. " +
            "단순 비율 변환은 사용하지 않습니다. targets.json에 이 크기의 프로필을 교정해 추가하세요.");
    }
}
