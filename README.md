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

Bugün var olan:

```
src/
  EnterpriseObservatory.Domain/               saf model + kurallar, sıfır I/O
  EnterpriseObservatory.Application/          use-case'ler, port arayüzleri
  EnterpriseObservatory.Collectors.Vsphere/   satıcı adaptörü
  EnterpriseObservatory.Api/                  BFF okuma yüzeyi (kütüphane)
  EnterpriseObservatory.Persistence.Sqlite/   gömülü durum ve ölçüm deposu
  EnterpriseObservatory.Host.AllInOne/        tek proses: toplama + web (MSI hedefi)
web/                                          React + TypeScript SPA (Tailwind v4)
tools/
  EnterpriseObservatory.VsphereProbe/         canlı vCenter'a karşı salt-okunur sonda
tests/
  EnterpriseObservatory.Domain.Tests/         hızlı, I/O yok
  EnterpriseObservatory.Application.Tests/    use-case testleri
  EnterpriseObservatory.Api.Tests/            projeksiyon testleri
  EnterpriseObservatory.Persistence.Sqlite.Tests/  yeniden başlatma ve aşağı örnekleme
  EnterpriseObservatory.Host.AllInOne.Tests/  bileşim testleri
  EnterpriseObservatory.Collectors.Vsphere.Tests/
  EnterpriseObservatory.Architecture.Tests/   katman sınırlarını CI'da zorlar
```

Henüz yazılmamış: çok-proses topoloji soyutlaması (`Hosting`). Mimaride yeri
olan ama bugün gerekmeyen bir parça; gerekçesi ADR-0001'de yazılı.

Bağımlılık yönü tek yönlüdür ve `Architecture.Tests` tarafından zorlanır:

```
Api ────────┐
            ├─→ Application ─→ Domain
Host ───────┘        ↑
Collectors ──────────┤
Persistence ─────────┘
```

`Api` hiçbir collector'ı göremez: görebilseydi önce bir vSphere ucu, sonra bir
iLO ucu büyür ve arayüz yeniden satıcı şeklinde parçalanırdı. Aynı şekilde
`Host` dışında hiçbir proje somut depolama motorunu göremez — SQLite seçimi bir
dağıtım kararı olarak kaldığı sürece değiştirilebilir. İkisi de birer test.

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

Arayüz ayrı bir SPA; `web/` kaynak, `wwwroot/` türetilmiş çıktıdır (commit
edilmez).

```bash
cd web && npm install && npm run build
```

Geliştirirken iki proses: host API'yi 5219'da sunar, Vite SPA'i kendi portunda
sunup `/api`'yi host'a proxy'ler. Tarayıcı yine tek köken görür, böylece çerez
davranışı üretimle aynı olur.

```bash
dotnet run --project src/EnterpriseObservatory.Host.AllInOne
cd web && npm run dev
```

vCenter parolası **hiçbir zaman** ayar dosyasına yazılmaz; host dosyadan gelen
bir parola görürse başlamayı reddeder (ADR-0010).

Her şey — okumalar dahil — kimlik doğrulama gerektirir. Yeni bir kurulumda hiç
hesap yoktur: servis açılışta tek kullanımlık bir **kurulum jetonu** loga yazar,
ve ilk yönetici o jetonla oluşturulur. Varsayılan parola yoktur ve ilk açılış
açık değildir (ADR-0014).

```
warn: This installation has no accounts yet ... setup token: 6ruVRxIS...
```

Roller: `Viewer` (görür), `Operator` (alarm onaylar/susturur/kapatır),
`Administrator` (ayrıca hesap ve yapılandırma). Her rol altındakini içerir.

Hesap yönetimi **Yapılandırma → Hesaplar** altında. Parolasını herkes kendi
değiştirebilir (mevcut parolayı sorarak); geri kalanı yöneticiye ait. Ürün son
yöneticinin silinmesini veya rolünün düşürülmesini reddeder — kimsenin
yönetemediği bir kurulum ancak veritabanı elle düzenlenerek onarılır.

### vCenter bağlantısı

İki yol var ve ikisi de desteklenir.

**Üründen (önerilen).** Yönetici olarak **Yapılandırma → Connections** →
*Add a vCenter*. Ad, adres, kullanıcı ve parola girilir; **Test** düğmesi
kaydetmeden önce tek bir deneme yapar ve ne olduğunu söyler. Parola şifrelenmiş
olarak saklanır ve hiçbir yerden geri okunamaz — düzenleme formu alanı boş
açar, boş bırakmak "saklı olanı koru" demektir (ADR-0015).

Bu yol, kabuk tırnaklaması sorununu ortadan kaldırdığı için önerilir:
PowerShell çift tırnak içinde `$` ve ters tırnağı yorumlar ve parolayı ürün
görmeden değiştirir.

**Yapılandırmadan.** Otomatik dağıtım için. Bu bağlantılar ekranda görünür ama
düzenlenemez — dağıtımı yapan taraf sahibidir ve bir düzenleme sonraki yeniden
başlatmada geri alınırdı.

```bash
dotnet user-secrets --project src/EnterpriseObservatory.Host.AllInOne set "VCenters:0:Password" '...'
```

> Tek tırnak kullanın. Çift tırnak içinde PowerShell `$` ile başlayan her şeyi
> değişken olarak yorumlar.

### Yedekleme

Parolalar `observatory.db` içinde şifreli durur; anahtar zinciri veritabanının
yanındaki `keys/` klasöründedir.

> **İkisini aynı yedeğe koymayın.** Aynı yedekte bulunmaları, korumayı tamamen
> ortadan kaldırır. Ayrı yedekleyin, ayrı erişim haklarıyla (ADR-0015).

Anahtarsız geri yüklenen bir veritabanı servisi durdurmaz: bağlantılar
"parola çözülemiyor" der, sorgulanmaz, ve parolalar yeniden girilir.

Katkı kuralları, commit formatı ve dal stratejisi için
[CONTRIBUTING.md](CONTRIBUTING.md).

## Lisans

Tescilli. Tüm hakları saklıdır.
