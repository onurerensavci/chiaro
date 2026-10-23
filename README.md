<p align="center">
  <img src="docs/logo.png" width="128" alt="Chiaro logo" />
</p>

<h1 align="center">Chiaro</h1>

<details>
<summary><b>🇹🇷 Türkçe için tıkla</b></summary>

<p><b>Philips Hue Bluetooth ampullerin için şık bir Windows kumandası. Köprü yok, bulut yok, sadece ışık.</b></p>

<p><i>Philips Hue Bluetooth ampuller için bağımsız bir uygulamadır; Philips/Signify ile bağlantısı yoktur.</i></p>

Chiaro, Philips Hue Bluetooth ampullerini **Hue Bridge olmadan**, doğrudan bilgisayarının Bluetooth'u ile Windows'tan kontrol etmeni sağlar.
Rengi, parlaklığı ve beyaz tonunu ayarla; ekranınla, müziğinle ya da günün saatiyle senkron çalıştır.

**Chiaro**, İtalyanca “ışık” demek; ressamların ışık ile gölge oyunu *chiaroscuro*'dan gelir. Işık ile gölgenin düellosu, tıpkı Light ile L gibi.

> **Eski adı Hue Studio.** v3.2 ve öncesi *Hue Studio* adıyla yayımlandı; 3.3 ile ad **Chiaro** oldu (“Hue” Philips'in markası). v3.2'den geçmek için `Chiaro_v3.3.exe`'yi indirip çalıştırman yeter; ayarların korunur. Sonraki sürümler uygulamanın içinden gelir.

Bu, benim bu türdeki ilk projem; fikirden tasarıma her adımını **Grok Bot** ile birlikte geliştirdim.

![Chiaro ana pencere](docs/screenshots/ana-pencere.png)

## ✨ Özellikler

- 🪟 **Liquid Glass arayüz**: cam efektli kartlar, koyu ve açık tema, akıcı animasyonlar.
- 🎬 **Sahneler**: Film, Oyun, Okuma, Uyku, Odak, Romantik… Her sahnenin kendi emojisi var. Tek dokunuşla uygula, şu anki ışıkla üzerine yaz, yeniden adlandır, emojisini değiştir. Hazır sahneleri istediğin zaman varsayılana sıfırla.
- 🖥️ **Ekran senkronu**: ekrandaki renk anlık olarak ampule yansır. Alan, monitör, yumuşatma, yöntem ve canlılık ayarlanabilir.
- 🎵 **Ses**: ışık müziğin ritmine ve şiddetine göre tepki verir.
- 🎧 **Spotify / müzik**: çalan şarkının albüm kapağının rengini ampule taşır.
- 🌈 **Gökkuşağı**: yönü, paleti ve hızı ayarlanabilen renk döngüsü.
- 🔥 **Şömine**: kıvılcımlı, titreşen sıcak ateş efekti.
- ☀️ **Gün ışığı**: gün boyu doğal ışığı izleyen sıcaklık ve parlaklık.
- ⏱️ **Zamanlayıcı**: uyku sayacı, gün doğumu gibi yavaşça aydınlanan uyandırma ve haftalık programlar.
- 🔔 **Bildirim ışığı**: Discord, WhatsApp, Teams, Outlook bildirimlerinde ışık kısa süre yanıp söner, sonra önceki haline döner.
- ⚡ **Hızlı panel**: saat yanındaki simgeye sol tıkla; aç/kapa, parlaklık ve favoriler elinin altında. **Modlar / Sahneler** sekmeleriyle sahneyi uygula ya da modu başlat; etkin olan vurgulanır, son sekme hatırlanır.
- 🖱️ **Simgede tekerlekle parlaklık**: tepsi simgesinin üzerinde fare tekerleğini çevir.
- 📌 **Mini mod**: her zaman üstte duran ince çubuk. Modlar ve Sahneler sekmeleriyle açılır kompakt panel.
- 🎨 **45 hazır renk + renk çarkı**, HEX girişi ve favori renkler.
- 🔄 **Uygulama içi güncelleme**: yeni sürüm çıkınca başlıkta **Güncelle** düğmesi ve hızlı panelde küçük bir uyarı belirir. Sürüm notlarına bak, tek tıkla indir; dosya doğrulanır, uygulama kendini yeniler ve yeniden açılır. İstersen **Güncellemeleri denetle** ile elle de bakabilirsin.
- 🌍 **Türkçe ve İngilizce**: arayüz Windows diline göre Türkçe ya da İngilizce açılır; **Genel → Dil / Language** ile değiştir, yeniden başlatmadan anında uygulanır.
- 🚀 **Windows ile başlat**: açılışta sessizce saat yanında başlar.

## 📸 Ekran görüntüleri

| Açık tema | Hızlı panel |
| --- | --- |
| ![Açık tema](docs/screenshots/acik-tema.png) | ![Hızlı panel](docs/screenshots/hizli-panel.png) |

| Hızlı panel: Modlar | Güncelleme |
| --- | --- |
| ![Hızlı panel, Modlar](docs/screenshots/hizli-panel-modlar.png) | ![Güncelleme penceresi](docs/screenshots/guncelleme.png) |

**Mini mod: Modlar ve Sahneler**

![Mini mod, Modlar](docs/screenshots/mini-mod-modlar.png)
![Mini mod, Sahneler](docs/screenshots/mini-mod-sahneler.png)

**Mod ayarları**

![Modlar kartı](docs/screenshots/modlar-karti.png)

## ⬇️ İndir

En son sürümü **[Releases sayfasından](https://github.com/onurerensavci/chiaro/releases/latest)** indir: `Chiaro_v3.3.exe`.
Kurulum gerekmez; tek dosya, .NET dahil. Sonraki sürümleri uygulamanın içinden güncelleyebilirsin.

## 🧰 Gereksinimler

- Windows 10 (2004 / 19041 veya üstü) ya da Windows 11, 64 bit
- Bluetooth LE destekli bir Bluetooth adaptörü
- Bluetooth özellikli bir Philips Hue ampul (kutusunda Bluetooth simgesi olanlar)

## 🚦 İlk çalıştırma

1. `Chiaro_v3.3.exe` dosyasını çalıştır. Uygulama imzalı olmadığı için Windows SmartScreen uyarı verebilir:
   **Daha fazla bilgi → Yine de çalıştır**.
2. Ampulü aç ve bilgisayara yaklaştır. Ampulün Bluetooth ile keşfedilebilir olması gerekir. Gerekirse telefondaki Hue uygulamasında
   *Ayarlar → Sesli asistanlar → Görünür yap* ile ampulü görünür yap ve telefonun Hue Bluetooth bağlantısını kapat.
3. **Tara**'ya bas, ampulünü seç ve **Bağlan**. Sonraki açılışlarda otomatik bağlanır.

## ⚠️ Bilinen sınırlamalar

- Yalnızca Bluetooth ile çalışır; Hue Bridge / Zigbee üzerinden kontrol yoktur.
- Aynı anda tek ampul kontrol edilir. Bluetooth menzili birkaç metredir.
- Telefondaki Hue uygulaması ampule bağlıyken bilgisayar bağlanamayabilir.
- Ekran ve ses senkronunda hız Bluetooth ile sınırlıdır (saniyede ~20 güncelleme).
- Spotify / müzik modu Windows medya oturumu sağlayan oynatıcılarla çalışır.
- Bildirim ışığı, Windows bildirim veritabanını salt okunur yoklar; yalnızca uygulama adını okur, içeriği okumaz.
- Uygulama kod imzalı değildir (SmartScreen uyarısı bu yüzden).
- Ayarlar `%LOCALAPPDATA%\HueBluetooth` klasöründe saklanır (eski adla aynı; Hue Studio'daki ayarların aynen kullanılır).
- Güncelleme denetimi açılışta en fazla günde bir kez GitHub'a yalnızca son sürümü sorar; başka veri gönderilmez. Exe'nin bulunduğu klasöre yazma izni gerekir (ör. Belgeler veya Masaüstü).

## 🛠️ Kaynaktan derleme

[.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir.

```bash
git clone https://github.com/onurerensavci/chiaro.git
cd chiaro/src/Chiaro
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Çıktı: `bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/Chiaro.exe`

## 💬 Geri bildirim

Kullandıkça eksiklerini bulup güncelleyeceğim; öneri ve hataları [Issues](https://github.com/onurerensavci/chiaro/issues)'a yazın.

## 🙌 Emeği geçenler

Fikir ve tasarım: **Onur Eren Savcı** • Geliştirme: **Grok Bot**

## ⚖️ Lisans ve feragat

[MIT](LICENSE) lisansı ile dağıtılır.

Chiaro, Philips Hue Bluetooth ampuller için bağımsız bir uygulamadır; Philips/Signify ile bağlantısı yoktur. Philips Hue, Signify'ın tescilli markasıdır.

</details>

<p align="center"><b>A sleek Windows remote for your Philips Hue Bluetooth bulbs. No bridge, no cloud, just light.</b></p>

<p align="center"><i>An independent app for Philips Hue Bluetooth bulbs; not affiliated with Philips or Signify.</i></p>

<p align="center">
  <a href="https://github.com/onurerensavci/chiaro/releases/latest"><img src="https://img.shields.io/github/v/release/onurerensavci/chiaro?label=download&color=0A84FF" alt="Latest release" /></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/license-MIT-30D158" alt="MIT" />
</p>

Chiaro lets you control Philips Hue Bluetooth bulbs from Windows **without a Hue Bridge**, straight over your computer's Bluetooth.
Set the color, brightness and white tone, and sync the light with your screen, your music or the time of day.

**Chiaro** is Italian for “light”, from *chiaroscuro*, the painters' play of light and shadow. A duel of light and shadow, like Light and L.

> **Formerly Hue Studio.** Versions up to 3.2 were released as *Hue Studio*; with 3.3 the name became **Chiaro** (“Hue” is a Philips trademark). To move from v3.2, just download and run `Chiaro_v3.3.exe`; your settings are kept. Later versions arrive from inside the app.

This is my first project of its kind; I built every step of it, from the idea to the design, together with **Grok Bot**.

![Chiaro main window](docs/screenshots/ana-pencere-en.png)

## ✨ Features

- 🪟 **Liquid Glass interface**: frosted-glass cards, dark and light themes, smooth animations.
- 🎬 **Scenes**: Movie, Gaming, Reading, Sleep, Focus, Romantic… Each scene has its own emoji. Apply one with a single tap, overwrite it with the current light, rename it or change its emoji. Reset the built-in scenes to their defaults whenever you like.
- 🖥️ **Screen sync**: the color on your screen flows to the bulb in real time. Area, monitor, smoothing, method and vividness are all adjustable.
- 🎵 **Audio**: the light reacts to the rhythm and loudness of your music.
- 🎧 **Spotify / music**: brings the album-cover color of the current track to the bulb.
- 🌈 **Rainbow**: a color cycle with adjustable direction, palette and speed.
- 🔥 **Fireplace**: a warm, flickering fire effect with sparks.
- ☀️ **Daylight**: color temperature and brightness that follow natural daylight through the day.
- ⏱️ **Timers**: a sleep timer, a wake-up that slowly brightens like a sunrise, and weekly schedules.
- 🔔 **Notification light**: the bulb briefly flashes for Discord, WhatsApp, Teams and Outlook notifications, then returns to how it was.
- ⚡ **Quick panel**: left-click the tray icon next to the clock for power, brightness and favorites at your fingertips. Use the **Modes / Scenes** tabs to apply a scene or start a mode; the active one is highlighted and the last tab is remembered.
- 🖱️ **Scroll for brightness**: turn the mouse wheel over the tray icon.
- 📌 **Mini mode**: a slim always-on-top bar, with a compact panel that opens from its Modes and Scenes tabs.
- 🎨 **45 preset colors + a color wheel**, HEX input and favorite colors.
- 🔄 **In-app updates**: when a new version is out, an **Update** button appears in the header and a small notice shows in the quick panel. Read the release notes and download with one click; the file is verified, the app updates itself and restarts. You can also check manually with **Check for updates**.
- 🌍 **English and Turkish**: the interface opens in English or Turkish depending on your Windows language; change it in **General → Dil / Language** and it applies instantly, without a restart.
- 🚀 **Start with Windows**: starts quietly in the tray at sign-in.

## 📸 Screenshots

| Light theme | Quick panel |
| --- | --- |
| ![Light theme](docs/screenshots/acik-tema-en.png) | ![Quick panel](docs/screenshots/hizli-panel-en.png) |

| Quick panel: Modes | Update |
| --- | --- |
| ![Quick panel, Modes](docs/screenshots/hizli-panel-modlar-en.png) | ![Update dialog](docs/screenshots/guncelleme-en.png) |

**Mini mode: Modes and Scenes**

![Mini mode, Modes](docs/screenshots/mini-mod-modlar-en.png)
![Mini mode, Scenes](docs/screenshots/mini-mod-sahneler-en.png)

**Mode settings**

![Modes card](docs/screenshots/modlar-karti-en.png)

## ⬇️ Download

Get the latest version from the **[Releases page](https://github.com/onurerensavci/chiaro/releases/latest)**: `Chiaro_v3.3.exe`.
No installation needed; it's a single file with .NET included. Later versions can be updated from inside the app.

## 🧰 Requirements

- Windows 10 (2004 / 19041 or later) or Windows 11, 64-bit
- A Bluetooth adapter with Bluetooth LE support
- A Bluetooth-capable Philips Hue bulb (the ones with the Bluetooth symbol on the box)

## 🚦 First run

1. Run `Chiaro_v3.3.exe`. Because the app isn't signed, Windows SmartScreen may show a warning:
   **More info → Run anyway**.
2. Turn the bulb on and bring it close to the computer. The bulb needs to be discoverable over Bluetooth. If needed, make it visible in the Hue app on your phone under
   *Settings → Voice assistants → Make visible*, and turn off the phone's Hue Bluetooth connection.
3. Press **Scan**, pick your bulb and press **Connect**. From then on it connects automatically.

## ⚠️ Known limitations

- Works over Bluetooth only; there is no control through a Hue Bridge / Zigbee.
- One bulb is controlled at a time. Bluetooth range is a few meters.
- The computer may not be able to connect while the Hue app on your phone is connected to the bulb.
- Screen and audio sync speed is limited by Bluetooth (about 20 updates per second).
- Spotify / music mode works with players that provide a Windows media session.
- The notification light polls the Windows notification database read-only; it reads only the app name, never the content.
- The app isn't code-signed (hence the SmartScreen warning).
- Settings are stored in `%LOCALAPPDATA%\HueBluetooth` (same as before, so your Hue Studio settings are used as they are).
- The update check asks GitHub only for the latest release, at most once a day at startup; no other data is sent. The folder containing the exe must be writable (e.g. Documents or Desktop).

## 🛠️ Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/onurerensavci/chiaro.git
cd chiaro/src/Chiaro
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/Chiaro.exe`

## 💬 Feedback

I'll keep finding what's missing and updating it as I use it; please post suggestions and bugs in [Issues](https://github.com/onurerensavci/chiaro/issues).

## 🙌 Credits

Idea and design: **Onur Eren Savcı** • Development: **Grok Bot**

## ⚖️ License and disclaimer

Distributed under the [MIT](LICENSE) license.

Chiaro is an independent app for Philips Hue Bluetooth bulbs; not affiliated with Philips or Signify. Philips Hue is a registered trademark of Signify.
