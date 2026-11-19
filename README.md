<p align="center"><img src="docs/logo.png" width="96" alt="DriveUP"></p>

<h1 align="center">DriveUP</h1>

<p align="center">Драйверы и настройка Windows в одном exe.<br>Drivers and Windows tuning in one exe.</p>

## Русский

- **Обновления** — драйверы из Windows Update одной кнопкой; новее на сайтах NVIDIA, Intel, Lenovo, Asus, HP, Dell (с проверкой подписи производителя и контрольной суммы, где её даёт производитель) и в каталоге Microsoft Update — отметьте несколько, скачайте очередью и установите разом на «Установке»; для AMD — ссылка на страницу драйвера; недостающие Visual C++, .NET и DirectX 9. Обновления BIOS не отмечаются сами и ставятся только с подтверждением и от сети. Если Windows Update не отвечает, проверка заканчивается через 4 минуты с понятной ошибкой.
- **Устройства** — все устройства, их драйверы и ошибки; драйвер для неизвестного устройства — из каталога Microsoft.
- **Установка** — перетащите папку, `.inf`, `.zip` или `.cab`.
- **Резервные копии** — сохранить драйверы и вернуть их после переустановки Windows.
- **Оптимизация** — режимы «Игры», «Работа» и «Тишина» под ваш компьютер; видно, что меняется и почему, рискованное — только с вашего согласия, любую настройку можно вернуть. Замер системы «до/после» — по желанию. Видна вся автозагрузка: незнакомые DriveUP программы — без галочки и только с вашего подтверждения. Настройки и удаление предустановленных приложений применяются ко всем пользователям компьютера (и к новым учётным записям); «Вернуть» возвращает каждому его прежние значения.
- Перед изменениями — точка восстановления Windows; если защита системы выключена, DriveUP предложит её включить.

Скачайте `DriveUP.exe` в [Releases](https://github.com/DenHaIz/DriveUP-exe/releases) и запустите. Установка не нужна, нужны права администратора. Настройки (`DriveUP.ini`) и журнал (`DriveUP.log`) хранятся рядом с exe — программу можно носить на флешке. С версии 1.4 программа одна: настройки и изменения прежнего `DriveUP_Pro.exe` подхватываются сами.

## English

- **Updates** — drivers from Windows Update in one click; newer ones on the NVIDIA, Intel, Lenovo, Asus, HP and Dell sites (the vendor's signature checked, and the checksum where the vendor publishes one) and in the Microsoft Update Catalog — tick several, download them in a queue and install them together on the Install tab; for AMD, a link to the driver page; missing Visual C++, .NET and DirectX 9. BIOS updates are never ticked by default and install only after a confirmation, on mains power. If Windows Update does not answer, the check ends after 4 minutes with a clear error.
- **Devices** — every device, its driver and its errors; a driver for an unknown device from the Microsoft catalog.
- **Install** — drop a folder, `.inf`, `.zip` or `.cab`.
- **Backups** — save drivers and bring them back after reinstalling Windows.
- **Tuning** — Games, Work and Quiet modes fitted to your computer; you see what changes and why, anything risky needs your yes, and every setting can be returned. Before/after measuring is optional. Every startup program is listed: ones DriveUP does not know are never ticked and need your confirmation. Settings and the removal of preinstalled apps apply to every user of the computer (and to new accounts); Return gives each user their own old values back.
- A Windows restore point before any change; if System Protection is off, DriveUP offers to turn it on.

Download `DriveUP.exe` from [Releases](https://github.com/DenHaIz/DriveUP-exe/releases) and run it. No install needed, administrator rights required. Settings (`DriveUP.ini`) and the journal (`DriveUP.log`) live next to the exe, so it travels on a USB stick. Since 1.4 there is a single edition: settings and changes made by the former `DriveUP_Pro.exe` are picked up automatically.

## Build · Сборка

`build.cmd` → `bin\DriveUP.exe` (.NET Framework 4.8, built into Windows). Developer notes: [HANDOFF.md](HANDOFF.md).

## License · Лицензия

[MIT](LICENSE) © DenHaIz
