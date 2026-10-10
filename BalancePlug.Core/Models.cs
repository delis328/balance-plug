using System.Globalization;
using System.Text.Json.Serialization;

namespace BalancePlug.Core;

public sealed record MascotItem(string Id, string Name);

public sealed class BubbleMotionSettings
{
    public double StartX { get; set; } = .16;
    public double StartY { get; set; } = .62;
    public double TargetX { get; set; } = .28;
    public double TargetY { get; set; } = .10;
    public double Speed { get; set; } = 55;
    public double Scale { get; set; } = 1;
    public BubbleMotionSettings Clone() => new() { StartX=StartX, StartY=StartY, TargetX=TargetX, TargetY=TargetY, Speed=Speed, Scale=Scale };
    public void Normalize()
    {
        StartX = Math.Clamp(double.IsFinite(StartX) ? StartX : .16, 0, 1); StartY = Math.Clamp(double.IsFinite(StartY) ? StartY : .62, 0, 1);
        TargetX = Math.Clamp(double.IsFinite(TargetX) ? TargetX : .28, 0, 1); TargetY = Math.Clamp(double.IsFinite(TargetY) ? TargetY : .10, 0, 1);
        Speed = Math.Clamp(double.IsFinite(Speed) ? Math.Round(Speed / 5) * 5 : 55, 10, 300);
        Scale = AppSettings.NormalizeScale(Scale);
    }
}


public sealed class AppSettings
{
    public string SiteUrl { get; set; } = BalanceApi.DefaultSite;
    public string Email { get; set; } = "";
    public string Theme { get; set; } = "Blue";
    public double BubbleScale { get; set; } = 1;
    public int UpdateIntervalSeconds { get; set; } = 60;
    public BubbleMotionSettings BubbleMotion { get; set; } = new();
    public double MascotScale { get; set; } = 1;
    public List<MascotItem> Mascots { get; set; } = new();
    public string? ActiveMascotId { get; set; }
    public bool Topmost { get; set; } = true;
    public SpendAlertSettings SpendAlerts { get; set; } = new();
    public SpendAlertProgress SpendProgress { get; set; } = new();
    public bool ClickThrough { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    // Read old configuration; omit these fields after migration.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Size { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CustomMascot { get; set; }

    public static bool ValidMascotId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static double NormalizeScale(double value) => !double.IsFinite(value) ? 1 : Math.Clamp(Math.Round(value * 20, MidpointRounding.AwayFromZero) / 20, .5, 2);
    public static double LegacyScale(string? size) => size == "Small" ? .8 : size == "Large" ? 1.2 : 1;
    public void Normalize()
    {
        try { SiteUrl = BalanceApi.NormalizeSite(SiteUrl ?? ""); }
        catch (BalanceException) { SiteUrl = BalanceApi.DefaultSite; }
        Email ??= "";
        (SpendAlerts ??= new()).Normalize(); SpendProgress ??= new();
        if (Theme is not ("Blue" or "Light" or "Dark")) Theme = "Blue";
        BubbleScale = NormalizeScale(BubbleScale); MascotScale = NormalizeScale(MascotScale);
        UpdateIntervalSeconds = Math.Clamp(UpdateIntervalSeconds <= 0 ? 60 : UpdateIntervalSeconds, 15, 3600);
        (BubbleMotion ??= new()).Normalize();
        Theme = "Blue";
        Mascots = (Mascots ?? new()).Where(m => m is not null && ValidMascotId(m.Id))
            .DistinctBy(m => m.Id).Select(m => m with { Name = string.IsNullOrWhiteSpace(m.Name) ? "本地形象" : m.Name.Trim() }).ToList();
        if (!Mascots.Any(m => m.Id == ActiveMascotId)) ActiveMascotId = null;
        if (Left is not null && !double.IsFinite(Left.Value)) Left = null;
        if (Top is not null && !double.IsFinite(Top.Value)) Top = null;
    }
}

public sealed class BalanceDisplay
{
    public decimal? Amount { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string Status { get; private set; } = "未登录 · 请右键配置";
    public string AmountText => Amount.HasValue ? BalanceApi.FormatBalance(Amount.Value) : "—";
    public string UpdatedText => UpdatedAt.HasValue ? "更新 " + UpdatedAt.Value.ToLocalTime().ToString("HH:mm:ss") : "尚未查询成功";
    public decimal? Success(decimal value)
    {
        var decrease = Amount.HasValue && value < Amount.Value ? Amount.Value - value : (decimal?)null;
        Amount = value; UpdatedAt = DateTimeOffset.Now; Status = "已更新";
        return decrease;
    }
    public static string FormatDecrease(decimal value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value < .000001m ? "-$<0.000001" : "-$" + value.ToString("0.00####", CultureInfo.InvariantCulture);
    }
    public void Fail(string message) { Status = "更新失败 · " + message; }
    public void Expire() { Status = "登录已失效 · 请右键配置"; }
    public void Reset() { Amount = null; UpdatedAt = null; Status = "查询中…"; }
}
