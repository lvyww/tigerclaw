using System;
using System.IO;
using System.Windows.Media;

namespace TigerClaw.Overlay
{
    internal sealed class TypingSoundPlayer : IDisposable
    {
        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int KeyPoolSize = 4;

        private readonly PlayerSlot[] _keyPool;
        private readonly PlayerSlot _spaceSlot;
        private readonly PlayerSlot _funcSlot;
        private int _nextKeyIndex;

        public TypingSoundPlayer()
        {
            string soundDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds");
            _keyPool = new PlayerSlot[KeyPoolSize];
            for (int i = 0; i < _keyPool.Length; i++)
            {
                _keyPool[i] = new PlayerSlot(Path.Combine(soundDir, "KeyNormal.wav"));
            }

            _spaceSlot = new PlayerSlot(Path.Combine(soundDir, "KeySpace.wav"));
            _funcSlot = new PlayerSlot(Path.Combine(soundDir, "KeyFunc.wav"));
        }

        public void Play(int vkCode, int volumePercent)
        {
            double volume = volumePercent / 100.0;
            if (volume < 0) { volume = 0; }
            if (volume > 1) { volume = 1; }

            ResolveSlot(vkCode)?.Play(volume);
        }

        private PlayerSlot ResolveSlot(int vkCode)
        {
            switch (vkCode)
            {
                case VK_SPACE:
                    return _spaceSlot;
                case VK_BACK:
                case VK_RETURN:
                case VK_ESCAPE:
                case VK_LSHIFT:
                case VK_RSHIFT:
                case VK_SHIFT:
                    return _funcSlot;
                default:
                    PlayerSlot slot = _keyPool[_nextKeyIndex];
                    _nextKeyIndex++;
                    if (_nextKeyIndex >= _keyPool.Length)
                    {
                        _nextKeyIndex = 0;
                    }

                    return slot;
            }
        }

        public void Dispose()
        {
            foreach (PlayerSlot slot in _keyPool)
            {
                slot?.Dispose();
            }

            _spaceSlot?.Dispose();
            _funcSlot?.Dispose();
        }

        private sealed class PlayerSlot : IDisposable
        {
            private readonly string _path;
            private MediaPlayer _player;
            private bool _needsRecreate;

            public PlayerSlot(string path)
            {
                _path = path;
            }

            public void Play(double volume)
            {
                if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                {
                    return;
                }

                if (!EnsurePlayer())
                {
                    return;
                }

                if (!TryPlay(volume))
                {
                    RecreatePlayer();
                    TryPlay(volume);
                }
            }

            private bool EnsurePlayer()
            {
                if (_player != null && !_needsRecreate)
                {
                    return true;
                }

                return RecreatePlayer();
            }

            private bool RecreatePlayer()
            {
                DisposePlayer();

                try
                {
                    _player = new MediaPlayer();
                    _player.MediaFailed += OnMediaFailed;
                    _player.Open(new Uri(_path, UriKind.Absolute));
                    _needsRecreate = false;
                    return true;
                }
                catch
                {
                    DisposePlayer();
                    _needsRecreate = true;
                    return false;
                }
            }

            private bool TryPlay(double volume)
            {
                if (_player == null)
                {
                    return false;
                }

                try
                {
                    _player.Stop();
                    _player.Volume = volume;
                    _player.Position = TimeSpan.Zero;
                    _player.Play();
                    return true;
                }
                catch
                {
                    _needsRecreate = true;
                    return false;
                }
            }

            private void OnMediaFailed(object sender, ExceptionEventArgs e)
            {
                _needsRecreate = true;
            }

            public void Dispose()
            {
                DisposePlayer();
            }

            private void DisposePlayer()
            {
                if (_player == null)
                {
                    return;
                }

                try
                {
                    _player.MediaFailed -= OnMediaFailed;
                    _player.Close();
                }
                catch
                {
                }

                _player = null;
            }
        }
    }
}
