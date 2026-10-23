<p align="center">
  <img src="docs/logo.png" width="128" alt="Chiaro logosu" />
</p>

<h1 align="center">Chiaro</h1>

<div align="center">

**Türkçe** | [English](README.md)

</div>

<p align="center"><b>Philips Hue Bluetooth ampullerin için şık bir Windows kumandası. Köprü yok, bulut yok, sadece ışık.</b></p>

<p align="center"><i>Philips Hue Bluetooth ampuller için bağımsız bir uygulamadır; Philips/Signify ile bağlantısı yoktur.</i></p>

<p align="center">
  <a href="https://github.com/onurerensavci/chiaro/releases/latest"><img src="https://img.shields.io/github/v/release/onurerensavci/chiaro?label=indir&color=0A84FF" alt="Son sürüm" /></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/lisans-MIT-30D158" alt="MIT" />
</p>

Chiaro, Philips Hue Bluetooth ampullerini **Hue Bridge olmadan**, doğrudan bilgisayarının Bluetooth'u ile Windows'tan kontrol etmeni sağlar.
Rengi, parlaklığı ve beyaz tonunu ayarla; ekranınla, müziğinle ya da günün saatiyle senkron çalıştır.

**Chiaro**, İtalyanca “ışık” demek; ressamların ışık ile gölge oyunu *chiaroscuro*'dan gelir. Işık ile gölgenin düellosu, tıpkı Light ile L gibi.

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
- Ayarlar `%LOCALAPPDATA%\HueBluetooth` klasöründe saklanır.
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
