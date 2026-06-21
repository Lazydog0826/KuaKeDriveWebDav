namespace KuaKeDriveWebDav.Quark;

/// <summary>
/// 夸克下载分片并发流：把一段字节范围按 PartSize 切成多片，以 Concurrency 路并发向夸克直链
/// 发起 Range 请求，按序拼合成单条只读流。滑动窗口（SemaphoreSlim）限制已启动未消费的分片数，
/// 内存占用约 Concurrency×PartSize；任一分片失败即取消其余在途请求并向上抛出。
/// 灵感来自 OpenList 的 net.Downloader——夸克对单连接大文件限速，需多连接并发才能跑满带宽。
/// </summary>
internal sealed class QuarkParallelDownloadStream : Stream
{
    private const int PartBodyMaxRetries = 3;

    private readonly Func<long, int, CancellationToken, Task<HttpResponseMessage>> _openPart;
    private readonly long _rangeStart;
    private readonly long _rangeLength;
    private readonly int _partSize;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _cts;

    private readonly Task<HttpResponseMessage>[] _parts;
    private readonly int _partCount;

    private int _nextToSchedule;
    private int _nextToConsume;
    private HttpResponseMessage? _current;
    private Stream? _currentStream;
    private long _position;
    private int _currentRetryCount;
    private bool _disposed;

    internal QuarkParallelDownloadStream(
        Func<long, int, CancellationToken, Task<HttpResponseMessage>> openPart,
        long rangeStart,
        long rangeLength,
        int partSize,
        int concurrency,
        CancellationToken externalCt
    )
    {
        _openPart = openPart;
        _rangeStart = rangeStart;
        _rangeLength = rangeLength;
        _partSize = partSize;
        _gate = new SemaphoreSlim(concurrency, concurrency);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        _partCount = (int)((rangeLength + partSize - 1) / partSize);
        _parts = new Task<HttpResponseMessage>[_partCount];
        Pump();
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_position >= _rangeLength)
            return 0;

        while (_position < _rangeLength)
        {
            if (_currentStream is null)
            {
                if (_nextToConsume >= _partCount)
                    return 0;
                // _cts 已链接外部取消令牌：等待分片响应时用它同时响应流取消与请求取消，
                // 避免每次 ReadAsync（下载热路径）都分配一个 linked CTS
                _current = await _parts[_nextToConsume].WaitAsync(_cts.Token);
                _currentStream = await _current.Content.ReadAsStreamAsync(cancellationToken);
            }

            var currentPartEnd = Math.Min((long)(_nextToConsume + 1) * _partSize, _rangeLength);
            var want = (int)Math.Min(buffer.Length, currentPartEnd - _position);
            int n;
            try
            {
                n = await _currentStream.ReadAsync(buffer[..want], cancellationToken);
            }
            catch (OperationCanceledException)
                when (!_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (await ReopenCurrentPartAsync(currentPartEnd, cancellationToken))
                    continue;
                throw;
            }
            catch
            {
                if (await ReopenCurrentPartAsync(currentPartEnd, cancellationToken))
                    continue;
                throw;
            }

            if (n > 0)
            {
                _position += n;
                return n;
            }

            if (_position < currentPartEnd)
            {
                if (await ReopenCurrentPartAsync(currentPartEnd, cancellationToken))
                    continue;
                throw new EndOfStreamException("夸克分片下载提前结束");
            }

            // 当前片读完，释放并补一个分片进窗口
            await DisposeCurrentAsync();
            _nextToConsume++;
            _currentRetryCount = 0;
            _gate.Release();
            Pump();
        }
        return 0;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("并发分片流仅支持异步读取，请使用 ReadAsync");

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// 启动尽可能多的分片请求，受并发窗口限制；同步非阻塞，窗口满则等下次推进补充。
    /// 每消费完一片会 Release 一个窗口槽并再次调用本方法，使已启动未消费的分片数恒定 ≤ Concurrency，
    /// 从而保证按序消费时下一片必已启动。
    /// </summary>
    private void Pump()
    {
        while (_nextToSchedule < _partCount && _gate.Wait(0))
        {
            var idx = _nextToSchedule++;
            var start = _rangeStart + (long)idx * _partSize;
            var len = (int)Math.Min(_partSize, _rangeLength - (long)idx * _partSize);
            var task = DownloadPartAsync(start, len);
            _parts[idx] = task;
            _ = task.ContinueWith(
                t =>
                {
                    if (_disposed && t is { IsCompletedSuccessfully: true })
                        t.Result.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    /// <summary>下载单个分片：成功返回上游响应（调用方按序消费其响应体）；失败时按 OpenList 策略重试数次</summary>
    private async Task<HttpResponseMessage> DownloadPartAsync(long start, int length)
    {
        for (var retry = 0; retry <= PartBodyMaxRetries; retry++)
        {
            try
            {
                var resp = await _openPart(start, length, _cts.Token);
                if (resp.IsSuccessStatusCode)
                    return resp;

                var statusCode = resp.StatusCode;
                resp.Dispose();
                throw new HttpRequestException($"夸克分片下载失败，状态码：{(int)statusCode}", null, statusCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (retry >= PartBodyMaxRetries)
                {
                    await _cts.CancelAsync();
                    throw new IOException("夸克分片下载重试失败", ex);
                }
                await Task.Delay(Random.Shared.Next(200, 501), _cts.Token);
            }
        }

        await _cts.CancelAsync();
        throw new IOException("夸克分片下载重试失败");
    }

    /// <summary>当前分片 body 读取失败时，从已消费位置继续打开剩余范围</summary>
    private async Task<bool> ReopenCurrentPartAsync(long currentPartEnd, CancellationToken cancellationToken)
    {
        if (_currentRetryCount >= PartBodyMaxRetries || _position >= currentPartEnd)
            return false;

        _currentRetryCount++;
        await DisposeCurrentAsync();

        var start = _rangeStart + _position;
        var length = (int)(currentPartEnd - _position);
        _current = await DownloadPartAsync(start, length);
        _currentStream = await _current.Content.ReadAsStreamAsync(cancellationToken);
        return true;
    }

    private async Task DisposeCurrentAsync()
    {
        if (_currentStream is not null)
            await _currentStream.DisposeAsync();
        _current?.Dispose();
        _currentStream = null;
        _current = null;
    }

    /// <summary>释放所有已成功下载但尚未消费的上游响应（在途请求由 _cts 取消终止）</summary>
    private void DisposeCompletedResponses()
    {
        foreach (var task in _parts)
            if (task is { IsCompletedSuccessfully: true })
                task.Result.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        if (disposing)
        {
            _currentStream?.Dispose();
            _current?.Dispose();
            DisposeCompletedResponses();
        }
        _cts.Dispose();
        _gate.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _cts.CancelAsync();
        await DisposeCurrentAsync();
        DisposeCompletedResponses();
        _cts.Dispose();
        _gate.Dispose();
        base.Dispose(false);
    }
}
