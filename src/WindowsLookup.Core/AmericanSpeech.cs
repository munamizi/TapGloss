using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace WindowsLookup;

/// <summary>在本机用唯一音色 Amy 合成并播放单词。</summary>
public sealed class AmericanSpeech : IDisposable
{
    private const int PlaybackCacheLimit = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _playbackGate = new(1);
    private readonly Dictionary<string, byte[]> _playbackCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _playbackOrder = new();
    private int _cachedBytes;
    private readonly PiperSpeech _piper = new();

    /// <summary>
    /// 用 Amy 离线合成英语单词。
    ///
    /// Args:
    ///     word - 需要朗读的原始单词，最多 80 个字符
    ///     cancellationToken - 取消本次结果，已经发出的合成会读完响应以便复用进程。
    /// Returns:
    ///     WAV 字节 - 给播放器使用的本地合成结果，Amy 不可用时抛出异常。
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string word, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        if (word.Length > 80 || word.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("朗读只接受最多 80 个字符的单词。", nameof(word));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await _piper._synthesizeAsync(word, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 用本地示例词静默预热 Amy 的模型和首次合成。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，现有模型可复用或后台预热尝试结束后完成。
    /// </summary>
    public Task PrepareAsync() => _piper._needsWarmup() ? Task.Run(_prepareAsync) : Task.CompletedTask;

    // 在后台完成首次推理并丢弃示例波形，失败后交由下次使用重新尝试。
    private async Task _prepareAsync()
    {
        try
        {
            await _piper._synthesizeAsync("test", CancellationToken.None, prewarmOnly: true).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Trace.TraceWarning("Amy 静默预热失败（{0}）。", ex.GetType().Name);
        }
    }

    /// <summary>
    /// 设置 Amy 进程的内存保留模式。
    ///
    /// Args:
    ///     mode - 动态续热或静态常驻。
    /// Returns:
    ///     无返回值，现有进程立即应用新的空闲规则。
    /// </summary>
    public void SetMemoryMode(SpeechMemoryMode mode) => _piper._setMemoryMode(mode);

    /// <summary>
    /// 在后台用 Amy 合成并播放一个单词，播放期间合并同一实例的重复点击。
    ///
    /// Args:
    ///     word - 要读出的原始英文单词
    ///     cancellationToken - 用于取消生成或停止正在播放的音频。
    /// Returns:
    ///     无返回值，异步任务在播放结束或重复点击被合并后完成。
    /// </summary>
    public async Task SpeakAsync(string word, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_playbackGate.Wait(0))
        {
            return;
        }
        try
        {
            _piper._notePlayback();
            byte[] wave = await _getPlaybackWaveAsync(word, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => _playWaveAsync(wave, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _piper._notePlayback();
            _playbackGate.Release();
        }
    }

    // 只缓存用于播放的 WAV，允许重听时跳过模型，也避免向外部暴露缓存数组。
    private async Task<byte[]> _getPlaybackWaveAsync(string word, CancellationToken token)
    {
        if (_playbackCache.TryGetValue(word, out byte[]? cached))
        {
            // 过期后重听仍可立即播放，同时为之后的查词重新预热。
            _ = PrepareAsync();
            return cached;
        }

        byte[] wave = await SynthesizeAsync(word, token).ConfigureAwait(false);
        if (wave.Length <= PlaybackCacheLimit)
        {
            while (_cachedBytes > PlaybackCacheLimit - wave.Length)
            {
                string oldest = _playbackOrder.Dequeue();
                _cachedBytes -= _playbackCache[oldest].Length;
                _playbackCache.Remove(oldest);
            }
            _playbackCache.Add(word, wave);
            _playbackOrder.Enqueue(word);
            _cachedBytes += wave.Length;
        }
        return wave;
    }

    // 播放内存中的 WAV，取消或失败时释放本次播放器和流。
    private static async Task _playWaveAsync(byte[] wave, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(wave);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        using var source = MediaSource.CreateFromStream(stream, "audio/wav");
        using var player = new MediaPlayer();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => completion.TrySetResult();
        player.MediaFailed += (_, _) => completion.TrySetException(new IOException("Amy 音频播放失败。"));
        try
        {
            token.ThrowIfCancellationRequested();
            player.Source = source;
            player.Play();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
        finally
        {
            player.Source = null;
        }
    }

    /// <summary>
    /// 释放 Amy 的本地合成进程。
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose() => _piper.Dispose();
}
