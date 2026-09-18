# Enterprise Observatory

Veri merkezi altyapısı için uçtan uca teşhis ve gözlemlenebilirlik platformu.
Hipervizör, out-of-band donanım (BMC), yönetim appliance'ı ve SAN/depolama
katmanlarını **tek bir host kimliğinde** birleştirir.

> **Durum:** Erken geliştirme. İlk dikey dilim (vSphere → alarm → UI) üzerinde
> çalışılıyor. Üretim kullanımına hazır değildir.

---

## Neden bu ürün var?

Geleneksel izleme araçları altyapıyı silolar halinde görür:

- Bir araç vCenter'ı izler, fiziksel HBA veya SFP durumundan habersizdir.
- Başkası SAN switch portlarını izler, hangi VM'in etkilendiğini bilmez.
- Storage yazılımı LUN gecikmesini raporlar, sorunun kirli bir fiber kablodan mı
  yoksa yanlış MPIO politikasından mı geldiğini söyleyemez.

Sonuç, sahada herkesin bildiği "suçlama çıkmazı": sunucu ekibi storage'ı,
storage ekibi switch'i suçlar, kesinti uzar.

Enterprise Observatory bu hattı tek parça olarak ele alır ve *hatanın tam olarak
nerede olduğunu* söylemeyi hedefler.

## Değişmez ilkeler

Bu ürünün mimarisi beş ilkeye dayanır. Bunlar tercih değil, kısıttır — her
mimari karar bunlara uymak zorundadır.

### 1. Asla uydurma

Bir endpoint'e ulaşılamıyorsa sonuç "sağlıklı" değildir. Boş envanter sağlıklı
gösterilmez. Bilinmeyen durum `Unknown`'dır ve açıkça öyle raporlanır.

> Yeşil gösterip aslında bilmeyen bir izleme aracı, hiç olmayan araçtan kötüdür.
> Operatörün araca duyduğu güven, ürünün tek gerçek varlığıdır.

### 2. Katman başına tek gerçek kaynağı

ESXi host listesinin tek kaynağı vCenter'dır. BMC collector'ları asla host satırı
yaratmaz; yalnızca mevcut bir host'a donanım telemetrisi ekler. Bu kural, çok
satıcılı izlemedeki en yaygın hatayı — aynı fiziksel kutunun üç kez sayılmasını —
yapısal olarak imkânsız kılar.

### 3. Kimlik katlama

Aynı fiziksel sunucu vCenter'da FQDN, iLO'da hostname, OneView'da seri numarası
olarak görünür. Korelasyon katmanı bunları tek kimliğe indirger. Eşleşmeyen BMC
kayıtları ayrı listede kalır; sahte host üretilmez.

### 4. Gürültü operatörün düşmanıdır

Her sinyal alarm değildir. Fingerprint tekilleştirmesi, hysteresis ve kalıcı
yaşam döngüsü durumu; aynı sorunun tekrar tekrar bildirilmesini engeller.
Operatör bir alarmı temizlediyse, koşul sürse bile yeniden bildirilmez.

### 5. İzleme aracı üretimi bozmaz

Altyapıya karşı varsayılan duruş salt-okunurdur. Yazma işlemleri açık yetki,
zaman sınırlı kira ve ikinci bir yetkilinin onayı olmadan çalışmaz.

## Mimari

Deployment-agnostik çekirdeğe sahip **modüler monolit**. Aynı kod tek proseste
(Windows MSI) veya ayrı proseslerde (konteyner, çok düğüm) çalışır; topoloji bir
yapılandırma seçeneğidir, mimari değişikliği değil.

```
src/
  EnterpriseObservatory.Domain/               saf model + kurallar, sıfır I/O
  EnterpriseObservatory.Application/          use-case'ler, port arayüzleri
  EnterpriseObservatory.Collectors.Vsphere/   satıcı adaptörü
  EnterpriseObservatory.Persistence/          depolama adaptörleri
  EnterpriseObservatory.Hosting/              topoloji soyutlaması
  EnterpriseObservatory.Host.AllInOne/        tek proses (MSI hedefi)
  EnterpriseObservatory.Web/                  API host + SPA sunumu
web/                                          React + TypeScript SPA (shadcn/ui, Tailwind v4)
tests/
  EnterpriseObservatory.Domain.Tests/         hızlı, I/O yok
  EnterpriseObservatory.Application.Tests/    use-case testleri
  EnterpriseObservatory.Architecture.Tests/   katman sınırlarını CI'da zorlar
```

Bağımlılık yönü tek yönlüdür ve `Architecture.Tests` tarafından zorlanır:

```
Web ─┐
     ├─→ Application ─→ Domain
Host─┘        ↑
Collectors ───┘
Persistence ──┘
```

`Domain` hiçbir projeye referans veremez. Bu kural bir konvansiyon değil, kırmızıya
düşen bir testtir.

## Mimari kararlar

Her önemli karar `docs/adr/` altında numaralı bir kayıttır: bağlam, karar,
sonuçlar ve reddedilen alternatifler. Bir şeyin neden böyle olduğunu merak
ediyorsan cevap oradadır.

Başlangıç noktası: [docs/adr/README.md](docs/adr/README.md)

## Geliştirme

```bash
dotnet build
dotnet test
```

Katkı kuralları, commit formatı ve dal stratejisi için
[CONTRIBUTING.md](CONTRIBUTING.md).

## Lisans

Tescilli. Tüm hakları saklıdır.
