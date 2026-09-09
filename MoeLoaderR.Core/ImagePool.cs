namespace MoeLoaderR.Core;

public sealed record ImagePool(int Id, string Name, string Sequence, int PostCount, string Url)
{
    public string DisplayText => $"Pool：{Name}（ID：{Id}）"
        + (string.IsNullOrEmpty(Sequence) ? "" : $" · #{Sequence}")
        + (PostCount > 0 ? $" · 共 {PostCount} 张" : "");
}
