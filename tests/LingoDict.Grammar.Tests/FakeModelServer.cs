using System.Net;
using System.Net.Http.Headers;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 假的下载服务端，直接接在 HttpClient 的处理器上。
/// </summary>
/// <remarks>
/// 不起真的 HttpListener：Windows 上非管理员注册 URL 前缀会被拒，CI 上更不确定。
/// 换成自定义 <see cref="HttpMessageHandler"/>，既没有端口和权限问题，
/// 也能精确地演出「Range 被无视」「传到一半断」这些真实世界里最难复现的情况。
/// </remarks>
internal sealed class FakeModelServer : HttpMessageHandler
{
    private readonly byte[] _body;

    public FakeModelServer(byte[] body) => _body = body;

    /// <summary>返回什么状态码；默认按请求正常响应。</summary>
    public HttpStatusCode? ForceStatus { get; set; }

    /// <summary>为真时无视 Range 头，永远回整个文件（有些 CDN 和代理就是这么干的）。</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>不为 null 时，发出这么多字节之后抛 IOException，模拟传到一半断线。</summary>
    public int? BreakAfter { get; set; }

    /// <summary>每发出一段就回调一次，参数是这次响应里已发出的累计字节数。</summary>
    public Action<long>? Sent { get; set; }

    /// <summary>收到的 Range 起点，null 表示那次请求没带 Range。</summary>
    public List<long?> SeenRanges { get; } = [];

    public int Requests => SeenRanges.Count;

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        SeenRanges.Add(from);

        if (ForceStatus is { } forced)
        {
            return Task.FromResult(new HttpResponseMessage(forced) { Content = new ByteArrayContent([]) });
        }

        var offset = IgnoreRange || from is null ? 0 : (int)from.Value;
        var slice = _body.AsSpan(offset).ToArray();

        var partial = offset > 0;
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new StreamContent(new FlakyStream(slice, BreakAfter, Sent)),
        };
        response.Content.Headers.ContentLength = slice.Length;
        if (partial)
        {
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(offset, _body.Length - 1, _body.Length);
        }

        return Task.FromResult(response);
    }

    /// <summary>读到一半就抛异常的流。</summary>
    private sealed class FlakyStream(byte[] data, int? breakAfter, Action<long>? sent) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (breakAfter is { } limit && _position >= limit)
            {
                throw new IOException("连接被对端关闭");
            }

            // 一次少给一点，让上层的进度回调和取消检查有机会跑到
            var take = Math.Min(Math.Min(count, 8192), data.Length - _position);
            if (breakAfter is { } cap)
            {
                take = Math.Min(take, cap - _position);
            }

            if (take <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            sent?.Invoke(_position);
            return take;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
