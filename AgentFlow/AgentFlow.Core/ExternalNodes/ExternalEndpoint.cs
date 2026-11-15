// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalEndpoint.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace AgentFlow.Core.ExternalNodes;

public interface IExternalEndpoint : IAsyncDisposable
{
    string Address { get; }
    Task<IExternalDuplexConnection> AcceptAsync(CancellationToken cancellationToken);
}

public interface IExternalDuplexConnection : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a Windows Named Pipe or Linux Unix Domain Socket endpoint.</summary>
public static class ExternalEndpointFactory
{
    public static IExternalEndpoint Create()
    {
        var id = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
            return new WindowsNamedPipeEndpoint(id);
        return new UnixDomainSocketEndpoint(id);
    }
}

internal sealed class WindowsNamedPipeEndpoint : IExternalEndpoint
{
    private readonly string _pipeName;
    private readonly NamedPipeServerStream _server;

    public WindowsNamedPipeEndpoint(string uniqueId)
    {
        _pipeName = $"AgentFlow-{uniqueId}";
        Address = @"\\.\pipe\" + _pipeName;
        _server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string Address { get; }

    public async Task<IExternalDuplexConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new StreamDuplexConnection(_server, ownsStream: true, maxLineBytes: ExternalProtocol.DefaultMaxLineBytes);
    }

    public ValueTask DisposeAsync()
    {
        return _server.DisposeAsync();
    }
}

internal sealed class UnixDomainSocketEndpoint : IExternalEndpoint
{
    private readonly Socket _listener;
    private readonly string _socketPath;

    public UnixDomainSocketEndpoint(string uniqueId)
    {
        _socketPath = Path.Combine(Path.GetTempPath(), $"agentflow-{uniqueId}.sock");
        Address = _socketPath;
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);
    }

    public string Address { get; }

    public async Task<IExternalDuplexConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new StreamDuplexConnection(new NetworkStream(socket, ownsSocket: true), ownsStream: true,
            maxLineBytes: ExternalProtocol.DefaultMaxLineBytes);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

internal sealed class StreamDuplexConnection : IExternalDuplexConnection
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly bool _ownsStream;
    private readonly int _maxLineBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public StreamDuplexConnection(Stream stream, bool ownsStream, int maxLineBytes)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _maxLineBytes = maxLineBytes;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null && Encoding.UTF8.GetByteCount(line) > _maxLineBytes)
            throw new InvalidDataException($"Worker message exceeds the {_maxLineBytes} byte line limit.");
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _writer.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_ownsStream)
            _writeGate.Dispose();
    }
}