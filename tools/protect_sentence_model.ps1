param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $true)]
    [ValidateSet('SentenceNgram', 'SentenceTransformer', 'SentenceVocabulary')]
    [string]$Kind,

    [Parameter(Mandatory = $true)]
    [string]$KeyFile
)

$ErrorActionPreference = 'Stop'

$kindIds = @{
    SentenceNgram = [byte]1
    SentenceTransformer = [byte]2
    SentenceVocabulary = [byte]3
}

function Get-DerivedKey {
    param(
        [byte[]]$MasterKey,
        [byte]$KindId,
        [string]$Purpose
    )

    $context = [Text.Encoding]::ASCII.GetBytes(('TigerClaw.Model.v1|{0}|{1}' -f $KindId, $Purpose))
    $hmac = [Security.Cryptography.HMACSHA256]::new($MasterKey)
    try {
        return ,$hmac.ComputeHash($context)
    }
    finally {
        $hmac.Dispose()
    }
}

$sourcePath = [IO.Path]::GetFullPath($Source)
$destinationPath = [IO.Path]::GetFullPath($Destination)
$keyPath = [IO.Path]::GetFullPath($KeyFile)
if (-not [IO.File]::Exists($sourcePath)) {
    throw "Source model not found: $sourcePath"
}
if (-not [IO.File]::Exists($keyPath)) {
    throw "Model protection key not found: $keyPath"
}

[byte[]]$masterKey = [IO.File]::ReadAllBytes($keyPath)
if ($masterKey.Length -ne 32) {
    throw 'Model protection key must contain exactly 32 bytes.'
}

$kindId = $kindIds[$Kind]
[byte[]]$encryptionKey = Get-DerivedKey -MasterKey $masterKey -KindId $kindId -Purpose 'enc'
[byte[]]$macKey = Get-DerivedKey -MasterKey $masterKey -KindId $kindId -Purpose 'mac'
[byte[]]$iv = New-Object byte[] 16
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($iv)
$random.Dispose()

[byte[]]$header = New-Object byte[] 36
[Text.Encoding]::ASCII.GetBytes('TCMODEL1').CopyTo($header, 0)
$header[8] = $kindId
$header[9] = 1
[BitConverter]::GetBytes([int64](Get-Item -LiteralPath $sourcePath).Length).CopyTo($header, 12)
$iv.CopyTo($header, 20)

$destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
}
$temporaryPath = $destinationPath + '.tmp'

try {
    $output = [IO.File]::Open($temporaryPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $output.Write($header, 0, $header.Length)
        $aes = [Security.Cryptography.Aes]::Create()
        try {
            $aes.KeySize = 256
            $aes.BlockSize = 128
            $aes.Mode = [Security.Cryptography.CipherMode]::CBC
            $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
            $aes.Key = $encryptionKey
            $aes.IV = $iv
            $encryptor = $aes.CreateEncryptor()
            try {
                $crypto = [Security.Cryptography.CryptoStream]::new(
                    $output,
                    $encryptor,
                    [Security.Cryptography.CryptoStreamMode]::Write,
                    $true)
                try {
                    $deflate = [IO.Compression.DeflateStream]::new(
                        $crypto,
                        [IO.Compression.CompressionLevel]::Optimal,
                        $true)
                    try {
                        $input = [IO.File]::OpenRead($sourcePath)
                        try {
                            $input.CopyTo($deflate)
                        }
                        finally {
                            $input.Dispose()
                        }
                    }
                    finally {
                        $deflate.Dispose()
                    }
                    $crypto.FlushFinalBlock()
                }
                finally {
                    $crypto.Dispose()
                }
            }
            finally {
                $encryptor.Dispose()
            }
        }
        finally {
            $aes.Dispose()
        }
    }
    finally {
        $output.Dispose()
    }

    $hmac = [Security.Cryptography.HMACSHA256]::new($macKey)
    try {
        $authenticated = [IO.File]::OpenRead($temporaryPath)
        try {
            [byte[]]$tag = $hmac.ComputeHash($authenticated)
        }
        finally {
            $authenticated.Dispose()
        }
    }
    finally {
        $hmac.Dispose()
    }

    $append = [IO.File]::Open($temporaryPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $append.Write($tag, 0, $tag.Length)
    }
    finally {
        $append.Dispose()
    }

    if ([IO.File]::Exists($destinationPath)) {
        [IO.File]::Delete($destinationPath)
    }
    [IO.File]::Move($temporaryPath, $destinationPath)
    Write-Host ('Protected {0}: {1} -> {2}' -f $Kind, $sourcePath, $destinationPath)
}
finally {
    if ([IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
    [Array]::Clear($masterKey, 0, $masterKey.Length)
    [Array]::Clear($encryptionKey, 0, $encryptionKey.Length)
    [Array]::Clear($macKey, 0, $macKey.Length)
}
