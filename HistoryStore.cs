using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermLens;

public sealed record HistoryEntry(string Term, string Context, Explanation Answer, DateTimeOffset CreatedAt)
{
    [JsonIgnore]
    public string Preview => Term.Length > 90 ? Term[..90].Replace('\n', ' ') + "…" : Term.Replace('\n', ' ');
    [JsonIgnore]
    public string Caption => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + (Context.Length > 0 ? " · 含上下文" : "");
}

// Called on the UI thread. A bounded, encrypted local cache; no credentials.
public sealed class HistoryStore
{
    public const int MaxEntries = 100;
    private const int MaxAnswerCharacters = 24000, MaxJsonBytes = 2_000_000, MaxFileBytes = 4_000_000;
    public static string DefaultPath => Path.Combine(SettingsStore.DirectoryPath, "history.dat");
    private readonly string? filePath;
    private readonly List<HistoryEntry> entries = new();
    private bool loadFailed;
    public IReadOnlyList<HistoryEntry> Entries => entries.AsReadOnly();
    public string Warning { get; private set; } = "";
    public event Action? Changed;

    public HistoryStore(string? filePath)
    {
        this.filePath = filePath;
        if (filePath == null || !File.Exists(filePath)) return;
        try
        {
            if (new FileInfo(filePath).Length > MaxFileBytes) throw new IOException();
            var json = SecretProtection.Unprotect(File.ReadAllText(filePath));
            if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new IOException();
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? throw new IOException();
            foreach (var entry in loaded.OrderByDescending(x => x.CreatedAt))
            {
                Validate(entry);
                if (!entries.Any(x => SameQuery(x, entry))) entries.Add(entry);
                if (entries.Count == MaxEntries) break;
            }
        }
        catch
        {
            entries.Clear(); loadFailed = true;
            Warning = "历史文件无法读取，原文件已保留；当前记录仅在本次运行可用。清空历史后可重新保存。";
        }
    }
    private static bool SameQuery(HistoryEntry a, HistoryEntry b)
        => string.Equals(a.Term, b.Term, StringComparison.OrdinalIgnoreCase) && a.Context == b.Context;
    private static void Validate(HistoryEntry entry)
    {
        if (entry == null || entry.Term == null || entry.Context == null || entry.Answer == null) throw new IOException();
        QueryRules.Validate(entry.Term, entry.Context);
        var a = entry.Answer;
        if (new[] { a.Chinese, a.English, a.Definition, a.Analogy, a.Usage, a.Meaning, a.Ambiguity, a.Principle, a.Example, a.Pitfalls }.Any(x => x == null)) throw new IOException();
        if (a.Plain != null ? string.IsNullOrWhiteSpace(a.Plain) : string.IsNullOrWhiteSpace(a.Chinese) || string.IsNullOrWhiteSpace(a.Definition)) throw new IOException();
        if (a.CopyText.Length is 0 or > MaxAnswerCharacters) throw new IOException();
    }
    public void Remember(string term, string context, Explanation answer)
    {
        var entry = new HistoryEntry(term.Trim(), context.Trim(), answer, DateTimeOffset.UtcNow);
        try { Validate(entry); }
        catch { Warning = "本次解释过长或格式不适合保存，未加入历史；仍可查看和复制。"; Changed?.Invoke(); return; }
        entries.RemoveAll(x => SameQuery(x, entry));
        entries.Insert(0, entry);
        if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        Save(); Changed?.Invoke();
    }
    public void Clear()
    {
        entries.Clear();
        // Clear explicitly deletes the old encrypted file, including an unreadable one.
        try
        {
            if (filePath != null) { File.Delete(filePath); File.Delete(filePath + ".tmp"); }
            loadFailed = false; Warning = "";
        }
        catch { Warning = "内存记录已清空，但无法删除本地历史文件，请检查目录写入权限。"; }
        Changed?.Invoke();
    }
    private void Save()
    {
        if (loadFailed) return;
        if (filePath == null) { Warning = ""; return; }
        try
        {
            var json = JsonSerializer.Serialize(entries);
            while (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes && entries.Count > 1)
            {
                entries.RemoveAt(entries.Count - 1);
                json = JsonSerializer.Serialize(entries);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(filePath + ".tmp", SecretProtection.Protect(json));
            File.Move(filePath + ".tmp", filePath, overwrite: true);
            Warning = "";
        }
        catch { Warning = "解释已加入本次历史，但暂时无法保存到本机；请检查目录写入权限。"; }
    }
}
