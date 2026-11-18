<div align="center">

# Noir

**Чёрно-белый музыкальный плеер для Windows 11**

Яндекс Музыка и SoundCloud без Electron: быстрее, легче, тише.

![C#](https://img.shields.io/badge/C%23-111?style=flat-square&logo=dotnet&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-111?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-111?style=flat-square&logo=windows11&logoColor=white)
![Яндекс Музыка](https://img.shields.io/badge/Яндекс%20Музыка-111?style=flat-square&logo=yandexcloud&logoColor=ffcc00)
![SoundCloud](https://img.shields.io/badge/SoundCloud-111?style=flat-square&logo=soundcloud&logoColor=ff5500)

<img src=".github/assets/noir.png" alt="Noir: Моя волна и очередь" width="900">

</div>

## Что умеет

- **«Моя волна»** с настройкой характера и **режимом CS2**: при запуске игры волна сама переключается на хайп-трэп, есть стоп-лист артистов.
- **Всё своё под рукой** — лайки, плейлисты, поиск, страницы артистов и альбомов, очередь «Далее» с перетаскиванием.
- **SoundCloud** — поиск, артисты, плейлисты, «станция» по треку (только открытые потоки, треки с DRM не играют).
- **Ровная громкость** — выравнивание по EBU R128 (LUFS), треки не скачут по уровню.
- **Часть системы** — звук через `Windows.Media.Playback.MediaPlayer`, поэтому медиаклавиши и карточка в [Island](https://github.com/trualontuu/island) работают сами; глобальные сочетания <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>←</kbd><kbd>→</kbd><kbd>↑</kbd><kbd>↓</kbd>.
- **Пульт с телефона** — [Noir для iPhone](https://github.com/trualontuu/noir-ios) управляет плеером, как Spotify Connect: пауза, треки, лайк, перемотка, громкость (связь через ретранслятор на Cloudflare, комната по аккаунту Яндекса).
- **Цвета** — строгий ч/б, тема Island или тон текущей обложки.
- **Лёгкий** — ~59 МБ памяти, 0% процессора в простое, трек стартует за ~0,4 с.

## Сборка

Часть кода общая с [Island](https://github.com/trualontuu/island) — файлы подключаются ссылками из соседней папки, поэтому оба репозитория должны лежать рядом:

```
projects/
  island/   ← trualontuu/island
  player/   ← этот репозиторий
```

```
dotnet publish -c Release -o dist
```

Проверка без окна: `Player.exe --snap <папка> [--play]` — снимки всех страниц и `report.txt` с замерами; `--play` включает волну на громкости 0 и без отзывов в аккаунт.

Настройки — `%APPDATA%\Player\settings.json`, кэш обложек — `%LOCALAPPDATA%\Player`.

> Noir — неофициальный клиент. Он не связан с Яндексом или SoundCloud и не обходит защиту контента.
