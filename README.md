# AnonymMailClient

[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://microsoft.com/windows)
[![Tor Network](https://img.shields.io/badge/Network-Tor%20Isolated-purple.svg)](https://www.torproject.org/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A lightweight Windows email client designed for complete privacy and anonymity. It operates exclusively through the Tor network, enabling disposable Guerrilla Mail inbox management and Direct-to-MX SMTP outbound delivery without requiring SMTP relay credentials or account registration.

---

## Key Features

- **Guerrilla Mail Integration over Tor**:
  - Instant random address generation or permanent custom username inbox claiming.
  - Supports all major Guerrilla Mail domains (`sharklasers.com`, `guerrillamail.com`, `grr.la`, `guerrillamail.net`, `guerrillamail.biz`, `spam4.me`, `pokemail.net`).
  - Read incoming mail, render HTML/text bodies, and delete messages.
- **Direct-to-MX Anonymous Sending**:
  - Resolves destination MX servers using DNS-over-HTTPS (DoH) via Tor SOCKS5 without leaking hostnames to local OS DNS.
  - Connects directly to the recipient's MX server on port 25 over Tor circuits (STARTTLS opportunistic encryption with plain SMTP fallback).
  - Automatically scrubs machine identifiers, timezone offsets, and client fingerprint headers from outgoing messages.
  - Dynamic Tor circuit rotation when exit nodes restrict outbound port 25.
- **System Tray & Background Monitoring**:
  - Minimizes to the system notification area.
  - Configurable periodic background mail checks (default 5 minutes).
  - Native Windows balloon/toast notifications upon receipt of new incoming email.
- **Robust Tor Lifecycle Management**:
  - Automatically discovers local Tor instances (Scoop, Tor Browser, Tor Expert Bundle, or PATH).
  - Automatically starts and monitors an app-managed Tor daemon with ephemeral ports if none is running.
  - Windows Job Object binding ensures the managed Tor child process terminates automatically when the application exits or is stopped.
- **Single Portable Executable**:
  - Self-contained single-file executable with zero installation footprint.
  - Clean portable storage (`UserData\` for accounts and cached messages, `AppCache\` for Tor data and logs).

---

## Architecture Overview

```
                      ┌──────────────────────────────────────────────┐
                      │              AnonymMailClient                │
                      │  (Modern Dark UI & System Tray Controller)   │
                      └──────────────────────┬───────────────────────┘
                                             │
                       Tor SOCKS5 Local Interface (Loopback)
                                             │
                      ┌──────────────────────▼───────────────────────┐
                      │              Tor Onion Router                │
                      │     (App-Managed with Circuit Isolation)     │
                      └──────────────┬────────────────┬──────────────┘
                                     │                │
            DoH & Guerrilla API      │                │  Port 25 (Direct-to-MX)
             over Tor Circuits       │                │   over Tor Circuits
                                     ▼                ▼
                         ┌─────────────────┐    ┌────────────────────┐
                         │ Guerrilla Mail  │    │ Recipient MX Host  │
                         │ HTTPS Endpoints │    │ (e.g. MX:25 SMTP)  │
                         └─────────────────┘    └────────────────────┘
```

---

## Installation & Running

Download the latest release from the [Releases](https://github.com/muhammetozeski/AnonymMailClient/releases) page:

- **Portable (Self-Contained)**: `AnonymMailClient.exe` — requires no pre-installed .NET runtime.
- **Framework-Dependent**: `AnonymMailClient-FrameworkDependent-RequiresNET10.exe` — requires [.NET Desktop Runtime 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

> **Digital Signature**: The executables are digitally signed. To let Windows verify the signature, run `Install-Certificate.cmd` from `SignatureTrust.zip` once. The programs run without it; only the signature stays unverified.

---

## Building from Source

Prerequisites:
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Tor installed or available on PATH (e.g., via Scoop: `scoop install tor`)

```powershell
# Clone the repository
git clone https://github.com/muhammetozeski/AnonymMailClient.git
cd AnonymMailClient

# Build the solution
dotnet build AnonymMailClient.sln -c Release

# Run the client
dotnet run --project AnonymMailClient/AnonymMailClient.csproj -c Release
```
