# DeckWinSave

[Русский](#русский) · [English](#english)

<p align="center"><img src="Docs/images/icon.png" width="180" alt="DeckWinSave icon"></p>

---

## Русский

**DeckWinSave — альтернатива Steam Cloud для «сторонних» игр. Легко переносите сохранения между Windows и Steam Deck в обе стороны и продолжайте игру с того же места!**

### Возможности

- **Подключение по SSH.** Простая настройка защищённого соединения между Windows и Steam Deck прямо в DeckWinSave.
- **Резервные копии.** Перед заменой сохранений DeckWinSave автоматически создаёт бэкап, чтобы можно было вернуть прежний прогресс.
- **Обмен файлами.** WinSCP уже в комплекте. Открывайте папки SteamOS из DeckWinSave и переносите файлы между устройствами.
- **В разработке.** Синхронизация сохранений через Яндекс Диск и Google Drive.

### Инструкция

Инструкция также доступна в приложении и через кнопку «Открыть в TXT».

#### 1. Подготовка Steam Deck

**1.1** Подключите PC и Steam Deck к одной сети. На Deck перейдите в режим рабочего стола и откройте Konsole.

**1.2** Узнайте адрес Steam Deck. Первая команда покажет локальное имя, вторая — IP-адреса. Если вводите IP, возьмите IPv4 из строки Wi-Fi или Ethernet.

```bash
hostname
ip -4 -br address show scope global
```

**1.3** Задайте пароль SteamOS (sudo), если его ещё нет.

```bash
passwd
```

**1.4** Включите SSH. DeckWinSave использует это защищённое соединение для доступа к файлам на Steam Deck.

```bash
sudo systemctl enable --now sshd
```

**1.5** Узнайте отпечаток SSH. Скопируйте значение целиком, начиная с SHA256:.

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

**1.6** Перед переходом на PC убедитесь, что у вас есть IP или локальное имя Steam Deck, пароль sudo и отпечаток SSH. Если у вас уже настроен туннель WireGuard к домашней сети, включите его и запишите IP Steam Deck внутри туннеля:

```bash
ip -4 -br address show type wireguard
```

#### 2. Подключение Steam Deck

**2.1** Откройте «Настройки» в DeckWinSave. Введите IP или локальное имя Steam Deck и отпечаток SSH. Имя пользователя SteamOS deck и порт SSH 22 заполнены по умолчанию. Если у вас другие значения, измените их.

**2.2** Если вы настроили туннель WireGuard к домашней сети, введите IP Steam Deck внутри туннеля в поле «Адрес для WG соединения (необязательно)».

**2.3** Нажмите «Проверить SSH». Если отпечаток совпал, нажмите «Сохранить». При первой привязке приложение может запросить пароль sudo для настройки доступа по ключу SSH. DeckWinSave не хранит этот пароль.

**2.4** Нажмите «Подключить Steam Deck». Зелёный индикатор — подключён, серый — отключён, красный — ошибка подключения.

**2.5** После подключения можно добавлять игры и просматривать папки Steam Deck через кнопку «Открыть WinSCP».

**2.6** Чтобы DeckWinSave подключался к Steam Deck при открытии, включите «Автоматически подключиться к Steam Deck при запуске» в разделе «Система» и нажмите «Сохранить».

**2.7** Чтобы сбросить привязку, в «Настройках» нажмите «Отвязать Steam Deck» и подтвердите действие. Профили игр, сохранения и бэкапы останутся.

#### 3. Добавление игры

**3.1** Используйте одну и ту же версию игры на обоих устройствах.

**3.2** Сделайте по одному сохранению на каждом устройстве, чтобы появились папки сохранений. Закройте игру.

**3.3** Нажмите «Добавить игру» и введите название.

**3.4** Чтобы добавить иконку игры, укажите её .exe на PC.

**3.5** В строке PC нажмите «Выбрать папку…» и укажите папку с сохранениями.

**3.6** Нажмите «Открыть WinSCP» в левом меню. Найдите папку с сохранениями на Steam Deck и скопируйте путь из адресной строки. Для показа скрытых папок нажмите Ctrl + Alt + H. Вернитесь в DeckWinSave и нажмите «Вставить путь» в строке Steam Deck.

**3.7** Добавьте в «Исключения» файлы, которые нужно пропускать при переносе. Например, settings.ini с настройками для конкретного устройства. Нажмите «Добавить игру», чтобы сохранить профиль. Для уже добавленной игры нажмите «Сохранить изменения».

#### 4. Синхронизация сохранений

**4.1** Перед переносом или восстановлением закройте игру на обоих устройствах.

**4.2** Нажмите «Синхронизировать». При первом переносе выберите направление. Проверьте список файлов и подтвердите перенос. Перед заменой DeckWinSave автоматически создаст бэкапы.

**4.3** Чтобы задать направление вручную, в строке «Ручной перенос» нажмите «Выбрать направление…».

**4.4** Чтобы создать или восстановить бэкап, в строке «Работа с бэкапами» нажмите «Открыть…». Выберите действие и устройство. При восстановлении используется последний бэкап выбранного устройства.

### Скриншоты

Интерфейс тестовой сборки.

<p align="center"><img src="Docs/images/settings.png" width="520" alt="Настройки подключения DeckWinSave"></p>

<p align="center"><img src="Docs/images/game-sync.png" width="520" alt="Синхронизация сохранений в DeckWinSave"></p>

### Скачать

[Последний релиз DeckWinSave](https://github.com/sol669/DeckWinSave/releases/latest)

| Вариант | Компоненты |
|---|---|
| Лёгкий установщик | При необходимости скачивает .NET 8 Runtime и Windows App Runtime с серверов Microsoft |
| Автономный установщик | Установка без интернета, компоненты включены |
| Лёгкая Portable | Распакуйте ZIP и запустите `DeckWinSave.exe`. Нужны .NET 8 Runtime и Windows App Runtime 2.3.1 или новее |
| Автономная Portable | Распакуйте ZIP и запустите `DeckWinSave.exe`. Компоненты включены |

### Требования

- Windows 10 версии 2004 или новее либо Windows 11, x64.
- Интерфейс на WinUI 3, русский и английский языки.
- Для первой привязки нужен включённый компонент Windows «Клиент OpenSSH».

### Сторонние компоненты

Лицензии и исходники комплектного WinSCP 6.7.1 перечислены в [уведомлениях о сторонних компонентах](ThirdParty/NOTICE.txt).

### Сборка из исходников

Для сборки нужны .NET 8 SDK и Inno Setup. В PowerShell запустите `installer/Build-Release.ps1` с параметром `-Iscc`, указав путь к `ISCC.exe`. Четыре пакета и их контрольные суммы появятся в папке `release`.

---

## English

**DeckWinSave is a Steam Cloud alternative for third-party games. Easily transfer saves between Windows and Steam Deck in either direction and pick up right where you left off!**

### Features

- **SSH connection.** Set up a secure connection between Windows and Steam Deck directly in DeckWinSave.
- **Backups.** DeckWinSave automatically backs up your saves before replacing them, so you can restore your previous progress.
- **File sharing.** WinSCP is included. Open SteamOS folders from DeckWinSave and transfer files between devices.
- **In development.** Save synchronization through Yandex Disk and Google Drive.

### Setup guide

The guide is also available in the app and can be opened as a text file using **Open as TXT**.

#### 1. Steam Deck setup

**1.1** Connect the PC and Steam Deck to the same network. On the Deck, switch to Desktop Mode and open Konsole.

**1.2** Find the Steam Deck address. The first command shows the hostname, and the second lists IP addresses. If you use an IP address, take the IPv4 address from the Wi-Fi or Ethernet line.

```bash
hostname
ip -4 -br address show scope global
```

**1.3** Set a SteamOS (sudo) password if you do not have one yet.

```bash
passwd
```

**1.4** Enable SSH. DeckWinSave uses this secure connection to access files on the Steam Deck.

```bash
sudo systemctl enable --now sshd
```

**1.5** Read the SSH fingerprint. Copy the whole value starting with SHA256:.

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

**1.6** Before switching to the PC, make sure you have the Steam Deck hostname or IP address, sudo password, and SSH fingerprint. If you already have a WireGuard tunnel to your home network, turn it on and note the Deck's IP address inside the tunnel:

```bash
ip -4 -br address show type wireguard
```

#### 2. Connecting Steam Deck

**2.1** Open Settings in DeckWinSave. Enter the Deck's IP address or hostname and SSH fingerprint. The SteamOS username defaults to deck and the SSH port defaults to 22. Change them if your setup uses different values.

**2.2** If you have a WireGuard tunnel to your home network, enter the Deck's IP address inside the tunnel in WG connection address (optional).

**2.3** Click Check SSH. If the fingerprint matches, click Save. During initial pairing, DeckWinSave may ask for the sudo password to set up SSH key access. It does not store the password.

**2.4** Click Connect Steam Deck. Green means connected, gray means disconnected, and red means the connection failed.

**2.5** Once connected, you can add games and browse Steam Deck folders using Open WinSCP.

**2.6** To connect to the Steam Deck when DeckWinSave opens, turn on Automatically connect to Steam Deck on launch under System and click Save.

**2.7** To reset pairing, click Unpair Steam Deck in Settings and confirm. Game profiles, saves, and backups are kept.

#### 3. Adding a game

**3.1** Use the same game version on both devices.

**3.2** Make one save on each device to create the save folders. Close the game.

**3.3** Click Add game and enter the name.

**3.4** To add a game icon, select the game's .exe on the PC.

**3.5** In the PC row, click Choose folder and select the save folder.

**3.6** Click Open WinSCP in the sidebar. Find the save folder on Steam Deck and copy the path from the address bar. Press Ctrl + Alt + H to show hidden folders. In DeckWinSave, click Paste path in the Steam Deck row.

**3.7** Add files to skip to Exclusions. For example, exclude settings.ini if it contains device-specific settings. Click Add game to save the profile. For an existing profile, click Save changes.

#### 4. Save synchronization

**4.1** Close the game on both devices before transferring or restoring saves.

**4.2** Click Sync. Choose the direction for the first transfer. Review the file list and confirm the transfer. DeckWinSave automatically creates backups before replacing files.

**4.3** To choose the direction manually, click Choose direction in the Manual transfer row.

**4.4** To create or restore a backup, click Open in the Manage backups row. Choose an action and device. Restore uses the most recent backup for the selected device.

### Screenshots

Preview build interface.

<p align="center"><img src="Docs/images/settings.png" width="520" alt="DeckWinSave connection settings"></p>

<p align="center"><img src="Docs/images/game-sync.png" width="520" alt="DeckWinSave save synchronization"></p>

### Download

[Latest DeckWinSave release](https://github.com/sol669/DeckWinSave/releases/latest)

| Version | Components |
|---|---|
| Lightweight installer | Downloads .NET 8 Runtime and Windows App Runtime from Microsoft when needed |
| Offline installer | Installs without an internet connection and includes the required components |
| Lightweight portable | Extract the ZIP and run `DeckWinSave.exe`. Requires .NET 8 Runtime and Windows App Runtime 2.3.1 or later |
| Offline portable | Extract the ZIP and run `DeckWinSave.exe`. Required components are included |

### Requirements

- Windows 10 version 2004 or later, or Windows 11, x64.
- WinUI 3 interface available in English and Russian.
- The Windows OpenSSH Client feature must be enabled for initial pairing.

### Third-party components

The [third-party notices](ThirdParty/NOTICE.txt) list the licenses and corresponding source code for the bundled WinSCP 6.7.1.

### Building from source

Install the .NET 8 SDK and Inno Setup. In PowerShell, run `installer/Build-Release.ps1` with `-Iscc` set to the path of `ISCC.exe`. The four packages and their checksums are written to the `release` directory.
