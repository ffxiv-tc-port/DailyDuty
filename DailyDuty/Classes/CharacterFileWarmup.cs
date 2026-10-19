using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DailyDuty.Classes;

/// <summary>
/// 登入那一幀要在框架執行緒上開約 70 個該角色的設定／樣式檔。檔案沒有已快取的掃描結論時（例如防毒
/// 特徵碼剛更新），第一次開檔要等即時防護掃描完（每檔約 1～5 ms，之後同一個檔不再掃），整批落在登入幀上。
/// 這裡趁沒人登入時在背景把各角色目錄的 json 讀一遍，讓掃描先在背景做完。
/// </summary>
/// <remarks>
/// 只讀、內容丟掉，失敗或被取消只影響登入快慢。
/// 契約：讀的時候那個目錄不可以有人存檔 —— 存檔最後一步的換名遇到任何開著的讀取代碼都會失敗，
/// 所以只讀沒登入的角色，登入的第一步先 <see cref="Stop"/>。
/// <see cref="Start"/>／<see cref="Stop"/> 只從框架執行緒或插件建構子呼叫。
/// </remarks>
public sealed class CharacterFileWarmup : IDisposable {
    // 等的是正在讀的那一個檔：冷檔實測最多幾 ms。
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(100);

    private readonly string configDirectory;
    private readonly CancellationTokenSource disposeCancel = new();
    private CancellationTokenSource? passCancel;
    private Task? pass;

    public CharacterFileWarmup(string configDirectory) {
        this.configDirectory = configDirectory;
    }

    public void Dispose() => disposeCancel.Cancel();

    /// <param name="skipContentId">這個角色的目錄不讀（此刻登入中的角色）；0＝全部都讀。</param>
    public void Start(ulong skipContentId) {
        if (disposeCancel.IsCancellationRequested) return;
        if (pass is { IsCompleted: false }) return;

        passCancel?.Dispose();
        passCancel = CancellationTokenSource.CreateLinkedTokenSource(disposeCancel.Token);

        var token = passCancel.Token;
        pass = Task.Run(() => Run(skipContentId, token));
    }

    /// <summary>回來之後背景一定沒有開著的檔（逾時例外，會記 Warning）。</summary>
    public void Stop() {
        if (pass is not { IsCompleted: false } running) return;

        passCancel?.Cancel();

        if (!running.Wait(StopTimeout)) {
            Service.Log.Warning($"[CharacterFileWarmup] Background read did not stop within {StopTimeout.TotalMilliseconds:F0} ms; continuing the login anyway.");
        }
    }

    private void Run(ulong skipContentId, CancellationToken token) {
        var stopwatch = Stopwatch.StartNew();
        var buffer = new byte[16 * 1024];
        int folders = 0, files = 0, failed = 0;

        try {
            foreach (var directory in Directory.EnumerateDirectories(configDirectory)) {
                if (!ulong.TryParse(Path.GetFileName(directory), out var contentId)) continue;
                if (contentId is 0 || contentId == skipContentId) continue;

                folders++;

                foreach (var file in Directory.EnumerateFiles(directory, "*.json")) {
                    if (token.IsCancellationRequested) return;

                    try {
                        // 讓出寫入與刪除：萬一撞上使用者手動複製設定，不要擋到對方。
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        while (stream.Read(buffer, 0, buffer.Length) > 0) { }

                        files++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        failed++;
                    }
                }
            }
        }
        catch (Exception ex) {
            if (!token.IsCancellationRequested) {
                Service.Log.Warning(ex, "[CharacterFileWarmup] Failed; only the next login gets slower.");
            }

            return;
        }

        if (!token.IsCancellationRequested) {
            Service.Log.Debug($"[CharacterFileWarmup] Read {files} files in {folders} character folders in {stopwatch.Elapsed.TotalMilliseconds:F0} ms ({failed} unreadable)");
        }
    }
}
