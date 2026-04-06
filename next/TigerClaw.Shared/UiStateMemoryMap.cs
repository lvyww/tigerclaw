using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TigerClaw.Shared
{
    public sealed class UiStatePublisher : IDisposable
    {
        private const int HeaderSize = sizeof(long) + sizeof(long) + sizeof(int);
        private const int Capacity = 128 * 1024;

        private readonly object _sync = new object();
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly DataContractJsonSerializer _serializer = new DataContractJsonSerializer(typeof(OverlayUiState));
        private long _sequence;

        public UiStatePublisher()
        {
            _mmf = MemoryMappedFile.CreateOrOpen(RuntimeConstants.UiStateMmfName, Capacity);
            _view = _mmf.CreateViewAccessor();
        }

        public void Publish(OverlayUiState state)
        {
            if (state == null)
            {
                return;
            }

            byte[] payload = Serialize(state);
            if (payload == null)
            {
                return;
            }

            int maxPayload = Capacity - HeaderSize;
            if (payload.Length > maxPayload)
            {
                byte[] clipped = new byte[maxPayload];
                Buffer.BlockCopy(payload, 0, clipped, 0, maxPayload);
                payload = clipped;
            }

            lock (_sync)
            {
                long seq = ++_sequence;
                long tick = MonotonicClock.GetMilliseconds();
                _view.Write(0, seq);
                _view.Write(sizeof(long), tick);
                _view.Write(sizeof(long) * 2, payload.Length);
                _view.WriteArray(HeaderSize, payload, 0, payload.Length);
                _view.Flush();
            }
        }

        private byte[] Serialize(OverlayUiState state)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    _serializer.WriteObject(ms, state);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    public sealed class UiStateReader : IDisposable
    {
        private const int HeaderSize = sizeof(long) + sizeof(long) + sizeof(int);
        private const int Capacity = 128 * 1024;

        private readonly DataContractJsonSerializer _serializer = new DataContractJsonSerializer(typeof(OverlayUiState));
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;

        public bool TryRead(out OverlayUiState state, out long sequence, out long tick64)
        {
            state = null;
            sequence = 0;
            tick64 = 0;

            try
            {
                if (!EnsureMap())
                {
                    return false;
                }

                _view.Read(0, out sequence);
                _view.Read(sizeof(long), out tick64);
                _view.Read(sizeof(long) * 2, out int length);

                if (length <= 0 || length > (Capacity - HeaderSize))
                {
                    return false;
                }

                byte[] payload = new byte[length];
                _view.ReadArray(HeaderSize, payload, 0, payload.Length);
                state = Deserialize(payload);
                return state != null;
            }
            catch
            {
                DisposeMap();
                return false;
            }
        }

        public bool TryReadIfChanged(long previousSequence, out OverlayUiState state, out long sequence, out long tick64)
        {
            state = null;
            sequence = 0;
            tick64 = 0;

            try
            {
                if (!EnsureMap())
                {
                    return false;
                }

                _view.Read(0, out sequence);
                if (sequence == previousSequence || sequence == 0)
                {
                    return false;
                }

                _view.Read(sizeof(long), out tick64);
                _view.Read(sizeof(long) * 2, out int length);

                if (length <= 0 || length > (Capacity - HeaderSize))
                {
                    return false;
                }

                byte[] payload = new byte[length];
                _view.ReadArray(HeaderSize, payload, 0, payload.Length);
                state = Deserialize(payload);
                return state != null;
            }
            catch
            {
                DisposeMap();
                return false;
            }
        }

        private bool EnsureMap()
        {
            if (_view != null)
            {
                return true;
            }

            try
            {
                _mmf = MemoryMappedFile.OpenExisting(RuntimeConstants.UiStateMmfName);
                _view = _mmf.CreateViewAccessor();
                return true;
            }
            catch
            {
                DisposeMap();
                return false;
            }
        }

        private OverlayUiState Deserialize(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return null;
            }

            try
            {
                using (var ms = new MemoryStream(payload))
                {
                    object obj = _serializer.ReadObject(ms);
                    return obj as OverlayUiState;
                }
            }
            catch
            {
                return null;
            }
        }

        private void DisposeMap()
        {
            try
            {
                _view?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _view = null;
            }

            try
            {
                _mmf?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _mmf = null;
            }
        }

        public void Dispose()
        {
            DisposeMap();
        }
    }
}
