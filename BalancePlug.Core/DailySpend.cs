namespace BalancePlug.Core;

public sealed record SpendMessage(string Text, bool Selected);
public sealed class SpendAlertSettings
{
    public bool Enabled { get; set; }
    public decimal Threshold { get; set; } = 20;
    public List<SpendMessage> Messages { get; set; } = new() { new("今日花费超过预算啦，休息一下吧！",true) };
    public void Normalize()
    {
        Threshold = Math.Clamp(Threshold,0,1000000);
        Messages = (Messages ?? new()).Where(x=>x is not null && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x=>x with { Text=x.Text.Trim()[..Math.Min(x.Text.Trim().Length,100)] }).ToList();
    }
}
public sealed class SpendAlertProgress
{
    public string Account { get; set; } = "";
    public DateOnly Day { get; set; }
    public decimal Threshold { get; set; }
    public int LastNode { get; set; } = -1;
}
public static class DailySpend
{
    public static DateOnly BeijingDay(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
    public static string? Evaluate(decimal amount, DateOnly day, string account, SpendAlertSettings settings, SpendAlertProgress progress)
    {
        if (progress.Account!=account || progress.Day!=day || progress.Threshold!=settings.Threshold)
        { progress.Account=account;progress.Day=day;progress.Threshold=settings.Threshold;progress.LastNode=-1; }
        var choices=settings.Messages.Where(x=>x.Selected).ToArray();
        if(!settings.Enabled || choices.Length==0 || amount<=settings.Threshold)return null;
        var node=(int)Math.Min(int.MaxValue,Math.Floor((amount-settings.Threshold)/5m));
        if(node<=progress.LastNode)return null;
        progress.LastNode=node;
        return choices[Random.Shared.Next(choices.Length)].Text;
    }
}
