# HardwareMonitor — system monitoring plugin for Logitech / Loupedeck

A plugin (`.lplug4`) for Logitech MX Creative Console and compatible devices that shows live **CPU, GPU and RAM** stats from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) right on the device buttons.

## Why this plugin exists

I built this plugin for myself because I needed key system stats always in front of me. I looked through the existing solutions on the marketplace, but none fit my needs — neither in functionality nor in appearance. So I decided to write my own, with the help of AI.

## Features

- **CPU** — load, temperature, clock speed (MHz)
- **GPU** — load, temperature, VRAM usage (used / total)
- **RAM** — load, used / total, free memory
- Load-history sparkline on every card
- Color coding (green / amber / red) based on the value
- Real-time updates (every ~1.5 s)
- Universal hardware detection — works with AMD and Intel CPUs, NVIDIA and AMD GPUs

## Requirements

- Logitech MX Creative Console or a compatible Loupedeck device
- Windows
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) with the web server enabled (port 8085) — otherwise buttons show `Offline`

## Installation

1. Download `HardwareMonitor.lplug4` from the [releases](https://github.com/GavryschukOleg/HardwareMonitor/releases) page.
2. Double-click the file — Logi Plugin Service installs the plugin automatically.
3. Enable the web server in LibreHardwareMonitor (Settings → Web server → port 8085).
4. In Logi Options+, assign the **CPU**, **GPU** and **RAM** actions to buttons.

### Installation via command line

```powershell
logiplugintool install ./HardwareMonitor.lplug4
```

## Building from source

Requires the Logi Plugin Service SDK and .NET 10:

```powershell
dotnet build -c Release
logiplugintool pack ./bin/Release ./HardwareMonitor.lplug4
```

## Data source

The plugin reads `http://127.0.0.1:8085/data.json`. Without the LibreHardwareMonitor web server running, the buttons show `Offline`.

## Support the project

If you find this plugin useful and want to support development — a coffee donation is always welcome 🙂

[![Donate via PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://www.paypal.com/donate/?business=donicteck%40gmail.com&currency_code=USD)

## License

[MIT](LICENSE) © 2026 GO
