# ADR-0008: Tasarım token mimarisi ve kontrast kapısı

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0006 (web stack), ADR-0007 (bilgi mimarisi)

## Bağlam

Önceki ürünün renk sistemi ölçülebilir şekilde bozuktu. Denetimde hesaplanan
değerler:

| Öğe | Açık tema | Koyu tema | Gereken |
|---|---|---|---|
| `status-pill.warning` | **2.06:1** | 5.81:1 | 4.5 |
| `status-pill.healthy` | **2.64:1** | 4.50:1 | 4.5 |
| `status-pill.critical` | **3.10:1** | **3.94:1** | 4.5 |
| alert noktası `warning` | **2.24:1** | 6.96:1 | 3.0 |
| alert noktası `healthy` | **2.95:1** | 5.28:1 | 3.0 |
| Odak halkası | **1.78:1** | **2.15:1** | 3.0 |

Sebep tek bir tasarım hatasıydı: **durum başına tek bir renk tanımlanmıştı** ve
o renk hem metin, hem nokta, hem kenarlık olarak, hem açık hem koyu temada
kullanılıyordu. `site.css` içinde koyu tema bloğu `--bg`, `--surface`, `--text`,
`--muted` değerlerini geçersiz kılıyor ama `--healthy`, `--warning`,
`--critical`, `--accent`, `--blue` değerlerine **dokunmuyordu**. Bu beş renk
koyu zemin için seçilmişti ve açık temada aynen kullanılınca çöküyordu.

Bu matematiksel olarak zaten imkânsız: beyaz üzerinde 14px metin olarak okunan
bir amber ile koyu zeminde 8px nokta olarak okunan amber aynı açıklıkta olamaz.

İkinci ve daha önemli sorun: **bunu fark eden bir mekanizma yoktu.** Değerler
aylarca yanlış kaldı çünkü kimse ölçmedi.

## Karar

### 1. Durum başına renk değil, rol rampası

Her durum (healthy, warning, critical, info, unknown) için beş ayrı değer
tanımlanır:

| Rol | Kullanım | Eşik |
|---|---|---|
| `surface` | Pill / satır arka planı | — |
| `border` | Nokta, kenarlık, bar dolgusu | ≥ 3:1 (sayfa ve kart zemini) |
| `solid` | Yüksek vurgulu rozet dolgusu | — |
| `solidOn` | `solid` üzerindeki metin | ≥ 4.5:1 (solid) |
| `text` | `surface` üzerindeki metin | ≥ 4.5:1 (surface **ve** kart) |

5 durum × 5 rol × 2 tema = 50 değer. Her tema kendi değerlerini **eksiksiz**
tanımlar; hiçbir değer temalar arası paylaşılmaz.

### 2. Renk anlamdır; eylem rengi durum hue'su olamaz

Bir izleme ürününde renk durum taşır. Eylem butonu warning amber'ı veya info
mavisi olursa operatör durum ile eylemi karıştırır.

- `primary` (eylem) = **violet, hue 285** — hiçbir durumun hue'su değil
- `destructive` (yıkıcı eylem) = critical ile **aynı** hue — yıkıcı eylem
  gerçekten tehlikelidir, semantik doğru

Bu gerekçeyle `ui-ux-pro-max` tasarım sisteminin önerdiği turuncu accent
(`#EA580C`) reddedildi (ADR-0006).

### 3. Renk asla tek başına bilgi taşımaz

`ui-ux-pro-max` veritabanının en yüksek önem dereceli bulgusu ("Color Only",
Severity: High) gereği, durum her zaman **renk + ikon + metin** üçlüsüyle
ifade edilir. Önceki üründe 8×8px renkli nokta tek bilgi kaynağıydı ve
tamamlayıcısı yalnızca `title` attribute'üydü — hover-only, yani klavye,
dokunmatik ve ekran okuyucu için erişilemez.

### 4. Tek kaynak, üretilmiş CSS

`web/design/tokens.mjs` tek gerçek kaynağıdır. CSS elle yazılmaz;
`generate-css.mjs` ile üretilir ve üretilmiş dosya "ELLE DÜZENLEMEYİN"
başlığı taşır.

Gerekçe: önceki üründe 6.101 satırlık `site.css` içinde aynı token birden
fazla yerde farklı değerle tanımlanmıştı ve hangisinin geçerli olduğu
belirsizdi.

Çıktı shadcn theming kuralına uyar (ADR-0006): OKLCH değişkenleri `:root` ve
tema sınıfı altında eksiksiz, Tailwind v4'e `@theme inline` ile açık.

### 5. Kontrast ve gamut CI kapısıdır

`web/design/validate-contrast.mjs` her PR'da çalışır ve şunları doğrular:

- Yukarıdaki tablodaki her eşik
- Her rengin **sRGB gamut** içinde olması

Gamut kontrolü ilk çalıştırmada 17 ihlal yakaladı — sRGB'de gösterilemeyen,
tarayıcının kırpacağı ve tasarlanandan farklı görünecek renkler. Mavi hue
(245) özellikle dar gamutlu; chroma değerleri buna göre düşürüldü.

Eşiği geçmeyen bir değer build'i kırar.

## Değerlendirilen alternatifler

### Alternatif A: Hazır bir palet kütüphanesi (Radix Colors, Tailwind palette)

Doğrulanmış, bakımı başkasına ait, kontrast garantili.

Seçilmedi çünkü bu ürünün durum semantiği standart değil: `unknown` birinci
sınıf bir durum (README ilke 1 — bilinmeyen sağlıklı sayılmaz) ve hazır
paletlerde karşılığı yok. Ayrıca `border` rolünün hem kart hem sayfa zeminine
karşı 3:1 tutması gibi ürüne özgü kurallar var. Yine de rol rampası fikri
Radix'in yaklaşımından alınmıştır.

### Alternatif B: Tek renk + opaklık varyantları (önceki ürünün yaklaşımı)

En az değer, en basit zihinsel model.

Seçilmedi çünkü tam olarak ölçülen başarısızlığın kaynağı bu. Opaklık,
zemin değiştiğinde kontrastı korumaz.

### Alternatif C: Kontrastı elle kontrol etmek

Tasarım incelemesinde gözle bakmak.

Seçilmedi. ADR-0001 ve ADR-0002'deki aynı ilke: insan disiplini gevşer,
makine kontrolü gevşemez. Önceki üründe değerler aylarca yanlış kaldı çünkü
ölçen yoktu.

## Sonuçlar

### Olumlu

- Her durum rengi her iki temada ölçülmüş ve eşiği geçiyor.
- Gamut dışı renk tanımlamak imkânsız.
- Token değeri değiştirmek CSS'i yeniden üretmeyi zorunlu kılar (CI drift
  kontrolü); kaynak ile çıktı ayrışamaz.
- Eylem rengi ile durum rengi karışmaz.

### Olumsuz / kabul ettiğimiz bedel

- **50 değer, 5 değerden çok.** Yeni bir durum eklemek beş değer tanımlamayı
  ve doğrulamayı gerektiriyor.
- Değerler elle ayarlanıyor; gamut sınırına yakın hue'larda (özellikle mavi)
  deneme-yanılma gerekti. Otomatik gamut sıkıştırma ileride eklenebilir.
- Üretilmiş CSS repoda tutuluyor; drift kontrolü CI'a bağlı. Üretimi build
  adımına taşımak da mümkündü ama repoda görünür olması incelemeyi
  kolaylaştırıyor.
- Kontrast eşikleri WCAG AA'ya göre; AAA hedeflenmiyor. NOC ortamında düşük
  ışık ve uzak okuma için AA yeterli görülmedi diye bir sinyal gelirse
  yeniden değerlendirilmeli.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Wallboard'ın 3+ metreden okunması AA eşiklerinin yetersiz kaldığını
  gösterirse, wallboard için ayrı ve daha sıkı bir eşik seti tanımlanmalı.
- Renk körlüğü simülasyonu (protanopi/döteranopi) doğrulayıcıya eklenmeli;
  healthy/critical ayrımı kırmızı-yeşil eksenindedir ve şu an yalnızca ikon
  ve metin ile telafi ediliyor.
