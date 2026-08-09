# HardwareMonitor — плагін моніторингу системи для Logitech / Loupedeck

Плагін (`.lplug4`) для Logitech MX Creative Console та сумісних пристроїв, який показує живі показники **CPU, GPU та RAM** з [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) прямо на кнопках пристрою.

## Чому цей плагін існує

Я зробив цей плагін для себе — виникла потреба бачити ключові показники системи завжди перед очима. Переглянув існуючі рішення на маркетплейсі, але жодне не підходило повністю ні за функціоналом, ні за зовнішнім виглядом. Тому написав власний — з допомогою штучного інтелекту.

## Можливості

- **CPU** — навантаження, температура, частота (MHz)
- **GPU** — навантаження, температура, обсяг VRAM (used / total)
- **RAM** — навантаження, використано / всього, вільна пам'ять
- Спарклайн історії навантаження на кожній картці
- Кольорове кодування (зелений / жовтий / червоний) залежно від значення
- Оновлення в реальному часі (кожні ~1,5 с)
- Автовизначення заліза — працює з AMD та Intel CPU, NVIDIA, AMD GPU

## Вимоги

- Logitech MX Creative Console або сумісний Loupedeck-пристрій
- Windows (підтримка macOS закладена в проєкт)
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) з увімкненим веб-сервером (порт 8085) — інакше кнопки показуватимуть `Offline`

## Встановлення

1. Завантажте `HardwareMonitor.lplug4` з [релізів](https://github.com/GavryschukOleg/HardwareMonitor/releases).
2. Двічі клікніть файл — Logi Plugin Service встановить плагін автоматично.
3. Увімкніть веб-сервер у LibreHardwareMonitor (Settings → Web server → port 8085).
4. У Logi Options+ призначте дії **CPU**, **GPU**, **RAM** на кнопки.

### Встановлення через командний рядок

```powershell
logiplugintool install ./HardwareMonitor.lplug4
```

## Збірка з вихідного коду

Потрібен Logi Plugin Service SDK і .NET 10:

```powershell
dotnet build -c Release
logiplugintool pack ./bin/Release ./HardwareMonitor.lplug4
```

## Використання джерела даних

Плагін читає `http://127.0.0.1:8085/data.json`. Без запущеного веб-сервера LibreHardwareMonitor кнопки показують `Offline`.

## Підтримати проєкт

Якщо плагін вам знадобився і хочете підтримати розробку — завжди радий донат-каві 🙂

[![Підтримати через PayPal](https://img.shields.io/badge/PayPal-Підтримати-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/donicteck)

## Ліцензія

[MIT](https://opensource.org/licenses/MIT)
