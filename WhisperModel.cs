using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace SrtEditor;

internal static class WhisperModel
{
    private const long Size = 147_951_465;
    private const string Sha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";
    private static readonly Uri DownloadUri = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim DownloadGate = new(1);

    public static async Task<string> GetPathAsync(IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
    {
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "cache", "models", "ggml-base.bin");
            progress?.Report(new("Проверка модели Whisper…"));
            if (await IsValidAsync(path, cancellationToken).ConfigureAwait(false))
                return path;

            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.download");
            try
            {
                await DownloadAsync(temporary, progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: true);
                }
                catch (IOException)
                {
                    // Another editor process may have finished downloading the same model.
                    if (!await IsValidAsync(path, cancellationToken).ConfigureAwait(false))
                        throw;
                }

                return path;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task DownloadAsync(string destination, IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        const string message = "Скачивание модели Whisper (142 МиБ)…";
        progress?.Report(new(message, 0));
        try
        {
            using var response = await Http.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long announced && announced != Size)
                throw new InvalidDataException("Сервер вернул модель неожиданного размера.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long written = 0;
            int previousPercent = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > Size)
                    throw new InvalidDataException("Сервер вернул модель неожиданного размера.");

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                int percent = (int)(100 * written / Size);
                if (percent != previousPercent)
                    progress?.Report(new(message, previousPercent = percent));
            }

            if (written != Size || Convert.ToHexString(hash.GetHashAndReset()) != Sha256)
                throw new InvalidDataException("Скачанная модель повреждена. Попробуйте ещё раз.");
        }
        catch (HttpRequestException exception)
        {
            throw new IOException("Не удалось скачать модель Whisper. Проверьте подключение к интернету и повторите попытку.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Сервер модели слишком долго не отвечал. Попробуйте ещё раз.", exception);
        }
    }

    private static async Task<bool> IsValidAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 128 * 1024, useAsync: true);
            return stream.Length == Size &&
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == Sha256;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
