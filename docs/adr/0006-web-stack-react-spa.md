# ADR-0006: Web arayüzü React + TypeScript SPA

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (dağıtım kısıtı), ADR-0007 (bilgi mimarisi — hazırlanıyor)

## Bağlam

Önceki ürünün arayüzü ASP.NET Core Razor Pages + Bootstrap 5 idi. Yaklaşık 60
sayfa, `site.css` **6.101 satır**, `site.js` **2.277 satır**.

Bu iki sayının büyüklüğü tesadüf değil, yapısal. Razor Pages'in bileşen modeli
zayıftır: paylaşılan UI parçası ya partial view olur (durum taşıyamaz) ya tag
helper (sınırlı). Sonuçta tekrar eden arayüz mantığı tek bir global CSS ve tek
bir global JS dosyasında birikir. Denetimde görülen semptomlar bunun doğrudan
sonucuydu — tutarsız breakpoint'ler (`991px` ve `991.98px` aynı dosyada),
`prefers-reduced-motion` yalnızca 4 seçiciyi kapsıyor, aynı bileşenin farklı
sayfalarda farklı davranması.

Yeni ürünün arayüzü daha da talepkâr olacak: varlık grafiğinde gezinme,
korelasyonlu alarm gruplaması, binlerce satırlık tablolar, canlı güncelleme.
Bunlar gerçek bir bileşen modeli gerektiriyor.

Aynı zamanda ADR-0001'deki kısıt duruyor: ürün tek tıkla kurulan bir Windows
MSI paketi olmalı, ek appliance gerektirmemeli.

## Karar

Arayüz **React 19 + TypeScript** ile ayrı bir SPA olarak yazılır; ASP.NET Core
host'u hem statik SPA çıktısını sunar hem de SPA'in tükettiği API'yi sağlar.

- **Bileşen kütüphanesi:** shadcn/ui (Radix UI tabanlı) + Tailwind CSS v4
- **Tipografi:** Fira Sans (etiket/gövde) + Fira Code (kimlik, WWN, IP, UUID,
  sayısal sütunlar)
- **Görsel dil:** Minimalism / Swiss — grid tabanlı, yüksek kontrast, süsleme yok
- **Tema:** koyu tema birincil, açık tema tam destekli
- **Canlı güncelleme:** SignalR TypeScript istemcisi
- **Kimlik doğrulama:** aynı-köken çerez tabanlı (token `localStorage`'da
  tutulmaz)

## Gerekçe

React'in seçilme sebebi ekosistem genişliği ve olgun bileşen modeli. shadcn/ui
+ Radix kombinasyonu, erişilebilirlik davranışını (odak tuzağı, klavye
gezintisi, ARIA) kütüphane seviyesinde çözüyor — önceki üründe bunlar elle
yazılıyordu ve denetimde eksik oldukları görüldü.

`ui-ux-pro-max` veritabanından doğrulanan, doğrudan uygulanacak kurallar
(hepsi 2026-08-13'te doğrulanmış kayıtlar):

| Kural | Kaynak |
|---|---|
| Semantik renkler OKLCH CSS değişkeni olarak `:root` **ve** `.dark` altında **eksiksiz** tanımlanır; Tailwind v4'e `@theme inline` ile açılır | shadcn / Theming, Severity: High |
| Bileşende ham palet rengi kullanılmaz (`bg-blue-500` yasak) | shadcn / Theming, Severity: High |
| 100'den uzun listeler sanallaştırılır (`react-window` / `react-virtual`) | react / Performance, Severity: High |
| Yeniden kullanılabilir liste bileşenleri generic tiplenir | react / TypeScript |

Sanallaştırma kuralı bu ürün için özellikle kritik: binlerce VM satırı
gösterilecek ve önceki üründe tablolar tam DOM olarak render ediliyordu.

### `--design-system` çıktısından reddedilenler

Skill'in ürettiği tasarım sistemi önerisinin bir kısmı bu ürüne uymuyordu ve
uygulanmadı. Kayda geçiriyorum ki ileride yeniden gündeme gelmesin:

| Öneri | Neden reddedildi |
|---|---|
| Pattern: "Hero + Features + CTA" | Pazarlama sayfası kalıbı. Ops konsolunda hero ve CTA kavramı yok. |
| Accent rengi `#EA580C` (turuncu) | **Semantik çakışma.** Bir izleme ürününde turuncu `Warning` demektir. Turuncu bir eylem butonu, operatörün durum rengiyle eylem rengini karıştırmasına yol açar. |
| Tipografi: Outfit / Work Sans | Veritabanının kendi etiketi "portfolios, agencies, landing pages". Yoğun sayısal tabloda tabular figür ve rakam ayırt edilebilirliği gerekiyor. |
| Açık tema öncelikli palet | Birincil kullanım ortamı NOC; koyu tema öncelikli. |

Korunan tek öneri: **Style = Minimalism & Swiss Style**, veritabanında
"Best For: Enterprise apps, dashboards, professional tools" olarak işaretli ve
erişilebilirlik riski düşük.

## Değerlendirilen alternatifler

### Alternatif A: Blazor Server

Tek dil (C#), `Domain` modeline doğrudan erişim, ayrı API katmanı gerekmez,
canlı güncelleme yerleşik, MSI paketine ek build zinciri girmez.

Seçilmedi. Teknik olarak dağıtım kısıtına en uygun seçenekti, ancak ekosistem
genişliği ve bileşen kütüphanesi olgunluğu React'te belirgin şekilde daha
yüksek. Erişilebilir bileşen davranışını hazır alabilmek (Radix) ve büyük bir
tasarım/bileşen ekosistemine erişmek, aşağıdaki bedellere değer görüldü.

### Alternatif B: Razor Pages + Bootstrap (mevcut yaklaşım)

Bilinen yol, taşınabilir CSS/JS birikimi var.

Seçilmedi çünkü 6.101 satırlık CSS ve 2.277 satırlık JS tam olarak bu modelin
bileşen eksikliğinden büyüdü. Aynı yapıyla devam etmek aynı sonucu üretme
riskini taşıyor ve bu proje o sonucu tekrar etmemek için var.

## Sonuçlar

### Olumlu

- Gerçek bileşen modeli: tekrar eden UI mantığı global dosyalarda birikmez.
- Erişilebilirlik davranışı (odak tuzağı, klavye, ARIA) kütüphaneden gelir.
- Tasarım token'ları tek yerde, tema geçişi yapısal.
- Liste sanallaştırma, grafik, tablo için olgun kütüphaneler hazır.
- Arayüz ve backend bağımsız geliştirilebilir ve test edilebilir.

### Olumsuz / kabul ettiğimiz bedel

Bunlar gerçek maliyetler ve planlanmaları gerekiyor:

- **API katmanı artık zorunlu.** Blazor'da `Domain`'e doğrudan erişilebilirdi;
  şimdi SPA'in tükettiği bir BFF (backend-for-frontend) katmanı gerekiyor.
  Bu, sürdürülmesi gereken ek bir sözleşme yüzeyi.
- **MSI paketine Node build zinciri giriyor.** SPA derlenip `dist` çıktısı
  pakete gömülmeli. Derleme makinesinde Node gerekir; CI ve `build-msi`
  betiği buna göre kurulmalı. Ürünün "tek tıkla kurulum" vaadi son kullanıcı
  için değişmez, ama **derleme** tarafı karmaşıklaşır.
- **İki ekosistem, iki bağımlılık ağacı, iki güvenlik yüzeyi.** npm bağımlılık
  denetimi ve sürüm yükseltmeleri artık ayrı bir bakım kalemi.
- **Paket boyutu izlenmeli.** Ürünün rekabet argümanlarından biri hafifliği;
  JS bundle'ın kontrolsüz büyümesi bunu aşındırır. Bundle bütçesi CI'da
  ölçülmeli.
- **İlk yükleme gecikmesi.** Razor Pages sunucu tarafında render ediyordu;
  SPA'in ilk açılışı daha yavaş olabilir. NOC'ta ekran sürekli açık
  durduğu için etkisi sınırlı, ama ölçülmeli.
- **Kimlik doğrulama dikkat ister.** Çerez tabanlı aynı-köken seçildi; bu CSRF
  korumasını zorunlu kılar. Token'ı `localStorage`'da tutmak XSS yüzeyi
  yaratacağı için kabul edilmiyor.

### Bu kararı yeniden değerlendirmemiz gereken durum

- JS bundle boyutu veya ilk yükleme süresi ürünün hafiflik iddiasını ölçülebilir
  şekilde zedelerse, sunucu tarafı render (SSR) veya kısmi Blazor kullanımı
  değerlendirilmeli.
- MSI derleme zinciri Node bağımlılığı nedeniyle kırılgan hale gelirse,
  SPA çıktısının önceden derlenip sürümlenmiş bir artefakt olarak tüketilmesi
  düşünülmeli.
