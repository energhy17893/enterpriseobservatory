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
  EnterpriseObservatory.Persistence/          depo port sözleşmeleri
  EnterpriseObservatory.Persistence.Postgres/ durum ve ölçüm deposu (PostgreSQL 18)
  EnterpriseObservatory.Host.AllInOne/        tek proses: toplama + web (MSI hedefi)
web/                                          React + TypeScript SPA (Tailwind v4)
tools/
  EnterpriseObservatory.VsphereProbe/         canlı vCenter'a karşı salt-okunur sonda
tests/
  EnterpriseObservatory.Domain.Tests/         hızlı, I/O yok
  EnterpriseObservatory.Application.Tests/    use-case testleri
  EnterpriseObservatory.Api.Tests/            projeksiyon testleri
  EnterpriseObservatory.Persistence.Postgres.Tests/  yeniden başlatma ve aşağı örnekleme
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
`Host` dışında hiçbir proje somut depolama motorunu göremez — depolama motoru
bir dağıtım kararı olarak kaldığı sürece değiştirilebilir. İkisi de birer test.
Motor bugün PostgreSQL'dir (ADR-0016); bu kural sayesinde seçim Domain,
Application veya Api'de tek satır değiştirmeden yapıldı.

`Domain` hiçbir projeye referans veremez. Bu kural bir konvansiyon değil, kırmızıya
düşen bir testtir.

## Ürün kurgusu

Ürünün **ne olduğu**, hangi dört soruyu cevapladığı, teşhis merdiveni ve hangi
katmanlarda hangi best practice kontrollerinin yapılacağı:

> **[docs/product-architecture.md](docs/product-architecture.md)**

Bu belge koddan önce gelir. Yeni bir yetenek önerilmeden önce oraya bakılır;
orada tarif edilmemiş bir yetenek, inşa edilmeden önce oraya yazılır.

Yanındaki ölçülmüş referans: [vSphere sayaç haritası](docs/collectors/vsphere-counter-map.md)
— hangi sayacın **hangi nesnede** toplandığı, gerçek bir vCenter'dan çıkarılmış.
Yeni bir sayaç eklenmeden önce buraya bakılır; sayacın adı, onu nereden
isteyeceğinizi söylemez.

Üçüncüsü, neyin **gerçekten kanıtlandığı**:

> **[docs/live-verification.md](docs/live-verification.md)**

Canlı bir estate'e karşı ne ölçüldü, hangi sayılarla, nasıl tekrar edilir — ve
aynı dürüstlükle, neyin henüz doğrulanmadığı. Bu projede bulunan hataların çoğu
testleri geçip canlıda sessizce yanlış olan koddaydı; yeşil test bir kanıt
değildir.

Henüz karar verilmemiş olanlar `docs/proposals/` altında: kod yazılmadan önce
tartışılacak somut öneriler. Şu an açık olan:
[analiz ve uygunluk katmanı](docs/proposals/analysis-layer.md).

Dördüncüsü, **başkalarının zaten çözdüğü**:

> **[docs/reference-approaches.md](docs/reference-approaches.md)**

vROps/Aria, Dynatrace ve Prometheus'un aynı problemlere verdiği cevaplar —
neyi benimsediğimiz, neyi reddettiğimiz ve neden. Sıra şudur: **önce referans
(bilinen nedir), sonra ölçüm (burada ne doğru), sonra öneri.** İkisi çelişirse
ölçüm kazanır ve çelişki canlı doğrulamaya yazılır.

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

### Veritabanı — ilk kurulum

PostgreSQL gerekir ([ADR-0016](docs/adr/0016-external-infrastructure-dependencies.md),
"Kurulum artık iki adımdır"). İkinci adım — ürünü veritabanına bağlamak — artık
ürünün içindedir: hiçbir veritabanı yapılandırılmamış bir kurulumda servis
**kurulum modunda** açılır ve yalnızca bu makineden erişilebilen tek bir sayfa
sunar.

1. Servisi başlatın. Log şunu söyler: *No database is configured: the service is
   in setup mode, on loopback only.*
2. **Sunucunun kendisinde** (konsolda ya da RDP ile) bir tarayıcıda
   `http://localhost:<port>/setup` açın. Kurulum modu yalnızca 127.0.0.1 ve ::1
   dinler; başka bir makineden bağlanılamaz. Diğer her adres 503 "setup
   required" döner.
3. İki yoldan birini seçin:
   - **Veritabanını ve rolü oluştur.** Bir kerelik bir yönetici hesabı girilir;
     ürün kendi rolünü (süper kullanıcı değil, parolası üretilir ve kimseye
     gösterilmez) ve o rolün sahibi olduğu veritabanını oluşturur. Yönetici
     parolası o tek istekte kullanılır; hiçbir yere yazılmaz ve loglanmaz.

     > The setup admin needs CREATEDB and CREATEROLE, not superuser; on
     > PostgreSQL 16+ the product role owns the database, and setup achieves
     > that by taking SET on it temporarily.

   - **Var olan bir veritabanını kullan.** DBA'nızın oluşturduğu rol ve
     parolası girilir. Rol `SUPERUSER`, `CREATEROLE` ya da `CREATEDB` taşıyorsa,
     ya da şemasında tablo oluşturamıyorsa (PostgreSQL 15+ için rol veritabanının
     sahibi olmalı ya da `GRANT CREATE ON SCHEMA public` almış olmalı), kurulum
     DBA'ya iletilecek cümleyle reddeder.
4. Şema uygulanır, bağlantı anahtar halkasının yanına
   (`%ProgramData%\EnterpriseObservatory\keys\database-connection.json`, parola
   DPAPI ile şifreli) yazılır ve yeni bir bağlantıyla doğrulanır. Servis aynı
   süreçte normal moda geçer; sayfa bir dakika içinde yanıt almazsa servisi
   yeniden başlatın.
5. İlk yöneticiyi logdaki tek kullanımlık kurulum jetonuyla oluşturun (yukarıda).

Sonrasında **Yapılandırma → Connections** ekranındaki Veritabanı kartı bağlantıyı
gösterir (parolayı asla), test eder ve parolayı döndürür — yalnızca
yöneticiler için (ADR-0010 eki).

Geliştirme ortamı ve yapılandırmayla kurulmuş sunucular **değişmeden** çalışır:
`Database:*` ve user-secrets'taki parola, dosya yoksa kullanılır.

```bash
dotnet user-secrets --project src/EnterpriseObservatory.Host.AllInOne set "Database:Password" '...'
```

### Windows servisi olarak kurulum (G-SVC)

Bugün ürün WMI üzerinden başlatılan bir konsol süreci; bu, CTRL_CLOSE
sinyaline (0xC000013A) açıktır — aynı makinedeki test PostgreSQL'i bu sinyal
iki kez öldürdü. Ürün artık `Microsoft.Extensions.Hosting.WindowsServices` ile
gerçek bir Windows servisi olarak kaydedilebilir; `UseWindowsService`,
süreç Hizmet Denetim Yöneticisi (SCM) tarafından başlatılmadığı her yerde
(geliştirme, `dotnet run`, testler) sessizce hiçbir şey yapmaz.

**Servis kaydı ve kurtarma ayarı, yükseltilmiş bir kabukta, elle, bir kez
yapılır.** Bu depodaki hiçbir kod bir servis oluşturmaz, başlatmaz, durdurmaz
ya da sistem ayarı değiştirmez.

```powershell
sc create EnterpriseObservatory binPath= "C:\Program Files\EnterpriseObservatory\EnterpriseObservatory.Host.AllInOne.exe" start= auto
sc failure EnterpriseObservatory reset= 86400 actions= restart/60000
```

`sc failure` satırı olmadan servis bir kere çöker ve orada kalır: SCM'ye "24
saatlik pencerede sayıp, her çökmede 60 saniye sonra yeniden başlat" denir.

Kayıt sonrası, aşağıdakiler **sizin** (ajanın değil) elle doğrulayacağınız
kontroller:

- `sc query EnterpriseObservatory` → `RUNNING`.
- Süreç bir konsola bağlı değil (Görev Yöneticisi'nde "Servisler" sekmesinde,
  konsol oturumu yok).
- `logs\host-*.log` günlük olarak döner (aşağıda).
- Süreci öldürün (Görev Yöneticisi'nden, `taskkill /F`) → SCM 60 saniye içinde
  yeniden başlatır ve `/health` 200 döner.

**Günlük dosyası.** Konsol günlüğü değişmedi (geliştirmede olduğu gibi
kalıyor); servisin konsolu olmadığı için ayrıca kalıcı bir dosyaya da yazılır:

    C:\ProgramData\EnterpriseObservatory\logs\host-.log

Anahtar halkasıyla aynı ProgramData kökü — ADR-0020'nin nedeniyle aynı sebep:
bir yükseltmeyi ve bir yeniden başlatmayı atlatması gereken şey, ikisinin de
yazdığı yerde durmaz. Günlük tarihe göre döner (`host-20260923.log`), 14
dosya saklanır. Yol `Logging:File:Path` ile geçersiz kılınabilir.

**Test PostgreSQL'i (5433) için aynı reçete.** Aynı CTRL_CLOSE sorunu bu
sunucuyu da vurdu; `pg_ctl register` ile o da bir servis olur:

```powershell
pg_ctl register -N postgresql-test-5433 -D C:\pgtest\data -S auto
icacls C:\pgtest\data /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)F"
```

`icacls` satırı gerekli: `pg_ctl register` varsayılan olarak NETWORK SERVICE
hesabı altında çalışır, ve o hesabın veri dizinine yazma izni yoksa servis
"RUNNING" görünüp hemen çöker.

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
