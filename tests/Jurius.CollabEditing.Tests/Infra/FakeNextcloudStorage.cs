using System.Collections.Concurrent;
using Jurius.CollabEditing.Services;

namespace Jurius.CollabEditing.Tests.Infra;

/// <summary>
/// Nextcloud de mentira, em memória. Guarda os bytes de verdade do .docx (gerados
/// pelo DocIO), então "reabrir o documento" no teste é exatamente o que o serviço
/// faria: baixar, carregar e ler o texto.
/// </summary>
public sealed class FakeNextcloudStorage : INextcloudStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public bool IsConfigured => true;

    /// <summary>Quantas gravações chegaram ao "Nextcloud" — o painel conta as mesmas.</summary>
    public int UploadCount;

    public void Seed(string path, byte[] content) => _files[path] = content;

    public byte[] Read(string path) => _files.TryGetValue(path, out var bytes) ? bytes : null;

    public Task<Stream> DownloadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(relativePath, out var bytes))
        {
            throw new FileNotFoundException("Arquivo não existe no Nextcloud de teste.", relativePath);
        }
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public async Task UploadAsync(string relativePath, Stream content, CancellationToken cancellationToken = default)
    {
        content.Position = 0;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _files[relativePath] = buffer.ToArray();
        Interlocked.Increment(ref UploadCount);
    }
}
