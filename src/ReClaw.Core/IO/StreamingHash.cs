using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReClaw.Core.IO
{
    public sealed class StreamingHash : IDisposable
    {
        private readonly IncrementalHash _hasher;
        private bool _isFinalized;

        public StreamingHash()
        {
            _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public void Append(ReadOnlySpan<byte> data) => _hasher.AppendData(data);

        public async Task AppendAsync(Stream source, int bufferSize = 16 * 1024, CancellationToken cancellationToken = default)
        {
            var buffer = new byte[bufferSize];
            while (true)
            {
                int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                _hasher.AppendData(new ReadOnlySpan<byte>(buffer, 0, read));
            }
        }

        public byte[] GetHashAndReset()
        {
            if (_isFinalized) throw new InvalidOperationException("Already finalized");
            _isFinalized = true;
            return _hasher.GetHashAndReset();
        }

        public void Dispose() => _hasher.Dispose();
    }
}
