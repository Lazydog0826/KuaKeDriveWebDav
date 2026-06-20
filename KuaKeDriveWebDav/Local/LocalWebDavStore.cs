using KuaKeDriveWebDav.WebDav;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;

// ReSharper disable MemberCanBeMadeStatic.Local

namespace KuaKeDriveWebDav.Local;

/// <summary>
/// 本地文件系统 WebDAV 数据源：可读写，支持 PUT / MKCOL / DELETE / MOVE / COPY
/// </summary>
[AutofacDependency(typeof(LocalWebDavStore), ServiceLifetime = ServiceLifetime.Singleton)]
public sealed class LocalWebDavStore(IOptions<LocalOptions> options) : IWebDavStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    /// <inheritdoc />
    public WebDavCapabilities Capabilities => WebDavCapabilities.Read | WebDavCapabilities.Write;

    /// <inheritdoc />
    public Task<WebDavNode?> GetByPathAsync(string webDavPath, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(webDavPath);
        WebDavNode? node = null;
        if (Directory.Exists(physical))
            node = ToNode(physical, webDavPath, isDirectory: true);
        else if (File.Exists(physical))
            node = ToNode(physical, webDavPath, isDirectory: false);
        return Task.FromResult(node);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebDavNode>> ListChildrenAsync(WebDavNode node, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(node.Id);
        var list = new List<WebDavNode>();
        if (!Directory.Exists(physical))
            return Task.FromResult<IReadOnlyList<WebDavNode>>(list);

        foreach (var entry in Directory.EnumerateFileSystemEntries(physical))
        {
            var relative = ToRelativePath(entry);
            var isDir = Directory.Exists(entry);
            list.Add(ToNode(entry, relative, isDir));
        }
        return Task.FromResult<IReadOnlyList<WebDavNode>>(list);
    }

    /// <inheritdoc />
    public Task<WebDavContent> OpenReadAsync(WebDavNode node, string? rangeHeader, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(node.Id);

        var stream = new FileStream(
            physical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous
        );
        var total = stream.Length;

        long start = 0;
        long end = total - 1;
        var hasRange = TryParseRange(rangeHeader, total, out var rStart, out var rEnd);
        if (hasRange)
        {
            start = rStart;
            end = rEnd;
        }
        stream.Seek(start, SeekOrigin.Begin);

        int statusCode;
        long? contentLength;
        string? contentRange;
        if (hasRange)
        {
            statusCode = StatusCodes.Status206PartialContent;
            contentLength = end - start + 1;
            contentRange = $"bytes {start}-{end}/{total}";
        }
        else
        {
            statusCode = StatusCodes.Status200OK;
            contentLength = total;
            contentRange = null;
        }

        return Task.FromResult(
            new WebDavContent
            {
                StatusCode = statusCode,
                ContentType = null,
                ContentLength = contentLength,
                ContentRange = contentRange,
                // 有 range 时用限定长度的子视图，无 range 时直接用全流
                Stream = hasRange ? new RangeStream(stream, start, end) : stream,
            }
        );
    }

    /// <inheritdoc />
    public async Task<WebDavNode> PutAsync(string webDavPath, Stream content, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(webDavPath);
        var parent = Path.GetDirectoryName(physical);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"父目录不存在：{parent}");

        await using var fs = new FileStream(
            physical,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous
        );
        await content.CopyToAsync(fs, ct);
        return ToNode(physical, webDavPath, isDirectory: false);
    }

    /// <inheritdoc />
    public Task<WebDavNode> MkcolAsync(string webDavPath, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(webDavPath);
        var parent = Path.GetDirectoryName(physical);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"父目录不存在：{parent}");
        if (Directory.Exists(physical) || File.Exists(physical))
            throw new InvalidOperationException($"资源已存在：{webDavPath}");

        Directory.CreateDirectory(physical);
        return Task.FromResult(ToNode(physical, webDavPath, isDirectory: true));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string webDavPath, CancellationToken ct = default)
    {
        var physical = ResolvePhysical(webDavPath);
        if (File.Exists(physical))
        {
            File.Delete(physical);
            return Task.FromResult(true);
        }
        if (Directory.Exists(physical))
        {
            Directory.Delete(physical, recursive: true);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<WebDavNode> MoveAsync(
        string sourcePath,
        string destPath,
        bool overwrite,
        CancellationToken ct = default
    )
    {
        var source = ResolvePhysical(sourcePath);
        var dest = ResolvePhysical(destPath);
        EnsureOverwrite(dest, overwrite);

        if (Directory.Exists(source))
        {
            Directory.Move(source, dest);
            return Task.FromResult(ToNode(dest, destPath, isDirectory: true));
        }
        if (File.Exists(source))
        {
            File.Move(source, dest, overwrite);
            return Task.FromResult(ToNode(dest, destPath, isDirectory: false));
        }
        throw new FileNotFoundException($"源资源不存在：{sourcePath}");
    }

    /// <inheritdoc />
    public async Task<WebDavNode> CopyAsync(
        string sourcePath,
        string destPath,
        bool overwrite,
        CancellationToken ct = default
    )
    {
        var source = ResolvePhysical(sourcePath);
        var dest = ResolvePhysical(destPath);
        EnsureOverwrite(dest, overwrite);

        if (Directory.Exists(source))
        {
            CopyDirectory(source, dest, ct);
            return ToNode(dest, destPath, isDirectory: true);
        }
        if (File.Exists(source))
        {
            await using var src = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous
            );
            await using var dst = new FileStream(
                dest,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous
            );
            await src.CopyToAsync(dst, ct);
            return ToNode(dest, destPath, isDirectory: false);
        }
        throw new FileNotFoundException($"源资源不存在：{sourcePath}");
    }

    /// <inheritdoc />
    public string GetEtag(WebDavNode node) => $"W/\"{node.UpdatedAt}-{node.Size}\"";

    /// <summary>把 WebDAV 相对路径解析为受 root 约束的物理路径，越界抛 UnauthorizedAccessException</summary>
    private string ResolvePhysical(string webDavPath)
    {
        var relative = NormalizeRelative(webDavPath);
        var physical = Path.GetFullPath(
            Path.Combine(_root, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))
        );
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!physical.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && physical != _root)
            throw new UnauthorizedAccessException($"路径越界：{webDavPath}");
        return physical;
    }

    /// <summary>把物理路径转回相对 root 的 WebDAV 风格路径</summary>
    private string ToRelativePath(string physical)
    {
        var full = Path.GetFullPath(physical);
        var rel = full[_root.Length..].TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        return "/" + rel;
    }

    /// <summary>规整 WebDAV 路径：保证以 / 开头、去尾部斜杠（根除外）</summary>
    private static string NormalizeRelative(string webDavPath)
    {
        var p = webDavPath;
        if (!p.StartsWith('/'))
            p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>由物理路径与相对路径构建节点（根目录 Id 为空串）</summary>
    private WebDavNode ToNode(string physical, string relative, bool isDirectory)
    {
        if (isDirectory)
        {
            var dirInfo = new DirectoryInfo(physical);
            return new WebDavNode
            {
                Id = NormalizeRelative(relative),
                Name = Path.GetFileName(physical.TrimEnd(Path.DirectorySeparatorChar)),
                IsDirectory = true,
                Size = 0,
                UpdatedAt = ToMillis(dirInfo.LastWriteTimeUtc),
                CreatedAt = ToMillis(dirInfo.CreationTimeUtc),
            };
        }
        var fileInfo = new FileInfo(physical);
        return new WebDavNode
        {
            Id = NormalizeRelative(relative),
            Name = fileInfo.Name,
            IsDirectory = false,
            Size = fileInfo.Length,
            UpdatedAt = ToMillis(fileInfo.LastWriteTimeUtc),
            CreatedAt = ToMillis(fileInfo.CreationTimeUtc),
        };
    }

    /// <summary>解析 Range 头（如 "bytes=0-99" / "bytes=100-" / "bytes=-100"），越界返回 false</summary>
    private bool TryParseRange(string? rangeHeader, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = rangeHeader["bytes=".Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;

        var left = spec[..dash];
        var right = spec[(dash + 1)..];
        if (string.IsNullOrEmpty(left))
        {
            // 后缀范围：bytes=-100 取最后 100 字节
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, total - suffix);
            end = total - 1;
        }
        else
        {
            if (!long.TryParse(left, out var s) || s < 0 || s >= total)
                return false;
            start = s;
            if (string.IsNullOrEmpty(right))
            {
                end = total - 1;
            }
            else
            {
                if (!long.TryParse(right, out var e) || e < start)
                    return false;
                end = Math.Min(e, total - 1);
            }
        }
        return start <= end && total > 0;
    }

    /// <summary>目标已存在且不允许覆盖时抛异常，允许覆盖时先删除既有目标</summary>
    private void EnsureOverwrite(string dest, bool overwrite)
    {
        if (!overwrite && (File.Exists(dest) || Directory.Exists(dest)))
            throw new InvalidOperationException($"目标资源已存在：{dest}");
        if (File.Exists(dest))
            File.Delete(dest);
        else if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
    }

    /// <summary>递归复制目录</summary>
    private void CopyDirectory(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), ct);
        }
    }

    private static long ToMillis(DateTime utc) =>
        utc == DateTime.MinValue ? 0 : new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    /// <summary>限定读取范围的流包装：达到指定结束字节后返回 EOF</summary>
    private sealed class RangeStream(FileStream inner, long start, long end) : Stream
    {
        private readonly long _length = end - start + 1;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0)
                return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var remaining = _length - _position;
            if (remaining <= 0)
                return 0;
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await inner.ReadAsync(buffer[..toRead], cancellationToken);
            _position += read;
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
