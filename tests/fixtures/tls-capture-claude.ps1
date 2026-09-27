# 假的 Claude Code：先发一个非 TLS 连接（应被忽略），再把 FAKE_CLIENT_HELLO（base64）作为握手首包发到 ANTHROPIC_BASE_URL 的端口。
$ErrorActionPreference = 'Stop'
$url = [Uri]$env:ANTHROPIC_BASE_URL
if ($env:NO_PROXY -notlike '*localhost*') { exit 3 }

$noise = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $url.Port)
$plain = [Text.Encoding]::ASCII.GetBytes("GET / HTTP/1.1`r`n`r`n")
$noise.GetStream().Write($plain, 0, $plain.Length)
$noise.Close()

$client = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $url.Port)
$bytes = [Convert]::FromBase64String($env:FAKE_CLIENT_HELLO)
$stream = $client.GetStream()
$stream.Write($bytes, 0, $bytes.Length)
$stream.Flush()
Start-Sleep -Seconds 30
