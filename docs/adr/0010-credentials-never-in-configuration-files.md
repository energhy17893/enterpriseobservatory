# ADR-0010: Kimlik bilgileri yapılandırma dosyasında bulunmaz

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (bileşim kökü), ADR-0005 (collector sözleşmesi),
  README ilke 5

## Bağlam

Önceki ürün kimlik bilgilerini DPAPI ile şifreliyordu ve mekanizma **doğru
çalışıyordu.** Yine de bir vCenter yönetici parolası yıllarca düz metin olarak
diskte durdu.

Sebep şifrelemenin kırılması değildi. `VCenterEndpoints` adlı serbest metin bir
JSON alanı vardı; şifreleme alanın kendisine uygulanıyor, ama alanın *içine*
serileştirilmiş belgeye uygulanmıyordu. Parola o belgenin içindeydi. Koruma
katmanının etrafından dolaşan bir yol vardı ve o yol kullanıldığında kimseye
haber verilmiyordu.

Ders, "daha iyi şifreleyelim" değil. Ders şu: **bir güvenlik kuralı, ihlal
edildiğinde gürültü çıkarmıyorsa, kural değil temennidir.**

Bu, README ilke 5'in ("izleme aracı üretimi bozamamalı, ve bu teknik olarak
garanti edilmeli") kimlik bilgilerine uygulanmış hâli. İyi niyetle değil,
mekanizmayla.

## Karar

### 1. Bağlama tamamen sıradan bırakılır

Parola normal yapılandırma sistemiyle bağlanır. Kullanıcı gizlilikleri (user
secrets), ortam değişkenleri, bir key vault ve bağlanmış bir secret dosyası —
hepsi hiçbir özel destek olmadan çalışır. Yeni bir mekanizma icat etmiyoruz;
icat edilen mekanizma, önceki ürünün etrafından dolaşılan mekanizmasıydı.

### 2. Bağlandıktan sonra kaynağı sorulur

`CredentialSourceGuard`, yapılandırma köküne "bu anahtarı hangi sağlayıcı
verdi" diye sorar. Cevap bir dosya sağlayıcısıysa servis **başlamaz**.

Hata mesajı üç şey söyler ve bir şeyi söylemez:

- hangi anahtar,
- hangi dosya (tam yol; "appsettings.json" adında birden çok dosya vardır),
- nereye taşınacağı,
- **değerin kendisini asla.** Sızan bir sırrı yazdırarak raporlamak, log'u
  sırrın sızdığı bir sonraki yer yapar.

Ayrıca parolanın **döndürülmesi** gerektiğini söyler. Dosyadan çıkarmak, onu
yedeklerden, commit'lerden ve kopyalardan çıkarmaz. Değer harcanmıştır.

### 3. Sağlayıcılar görülemiyorsa reddedilir

Yapılandırma bir kök değilse (örneğin bir `IConfigurationSection` verilmişse)
koruma garantisini veremez ve bunu söyler. Kontrol ediyormuş gibi görünmek,
kontrol etmemekten kötüdür.

### 4. Bu kuralın yarısı hâlâ eksik

Burada kapatılan, **çalışma zamanı** yarısı: değer nereden geldi. Diğer yarısı
tip sistemine ait — kimlik bilgisi taşıyan her alanın tipiyle işaretlenmesi,
böylece bir sırrın serbest metin bir alanın içine serileştirilmesinin
*mümkün olmaması*. Önceki üründeki sızıntının fiili mekanizması tam olarak
buydu.

O yarı, kalıcılık katmanıyla birlikte gelecek ve kendi ADR'sini alacak. Bugün
kapatılmamasının sebebi, henüz serileştirilen bir yapılandırma modelimizin
olmaması.

## Sonuçlar

### Olumlu

- Sızıntının fiilen olduğu yol — dosyadaki düz metin — artık servisi durduruyor.
- Operatör, kuralın ne olduğunu belgeyi okuyarak değil, ihlal ettiğinde
  öğreniyor.
- Standart yapılandırma sağlayıcılarıyla çalıştığı için dağıtım şekli
  (Windows servisi, konteyner, MSI) kuralı değiştirmiyor.

### Olumsuz / kabul ettiğimiz bedel

- Geliştirme ortamında `dotnet user-secrets set` adımı bir sürtünme. Kasıtlı:
  sürtünmesiz olan yol, parolanın dosyada durduğu yoldu.
- Koruma yalnızca listelenen anahtarları denetler. Yeni bir kimlik bilgisi alanı
  eklenip listeye yazılmazsa denetlenmez — tip sistemi yarısı yazılana kadar
  kalan açık bu.
- Dosya sağlayıcısını tanıma, `Microsoft.Extensions.Configuration`'ın sağlayıcı
  listesine bağımlı. Bu API değişirse koruma sessizce hiçbir şey bulmaz hâle
  gelebilir; testler bunu yakalamak için gerçek bir JSON dosyası kullanıyor,
  taklit bir sağlayıcı değil.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Kalıcılık katmanı geldiğinde, kimlik bilgilerinin veritabanında nasıl
  duracağı ve tip sistemi yarısının nasıl uygulanacağı yeni bir ADR gerektirir.
- Kurulum sihirbazı üzerinden kimlik bilgisi girilmesi gerekirse, bu değerin
  hangi depoya yazılacağı yeniden karara bağlanır.

## Ek — Kurulum sihirbazı kararı (Setup wizard decision)

- **Tarih:** 2026-09-23
- **İlgili:** ADR-0014 (yerel hesaplar ve çerez oturumları), ADR-0015
  (üründe girilen bağlantılar), ADR-0016 (dış altyapı bağımlılıkları —
  "Kurulum artık iki adımdır"), ADR-0020 (anahtar halkasının yeri), yol
  haritası G-DB

Yukarıdaki son madde açık bırakılmıştı: kimlik bilgisi bir kurulum sihirbazından
girilirse, hangi depoya yazılır? G-DB ile bu soru gerçek oldu — ADR-0016'nın iki
adımlı kurulumunun ikinci adımı (ürünü veritabanına bağlamak) artık ürünün
içinde, pgAdmin ve Grafana'nın yaptığı gibi: bir sayfa, bir kez.

### Depo: anahtar halkasının yanındaki korumalı dosya

Veritabanı bağlantısı `Storage:KeyRingPath` dizininin **içindeki**
`database-connection.json` dosyasına yazılır (varsayılan
`%ProgramData%\EnterpriseObservatory\keys\database-connection.json`). Host,
port, veritabanı, rol ve şema düz metindir; parola, her vCenter parolasını
koruyan aynı `ISecretProtector` ile (Data Protection, Windows'ta makine
kapsamlı DPAPI) ve ayrı bir amaç dizgisiyle
(`EnterpriseObservatory.Database.Password.v1`) korunmuş şifreli metindir.

Önceki ürünün **mekanizması** doğruydu — DPAPI. Yanlış olan **yeriydi**; yer
ADR-0020'de karara bağlandı ve dosya o dizinin içinde durduğu için açılıştaki
dayanıklılık denetimi onu da kapsar. Anahtarsız işe yaramaz, bu yüzden
anahtarlarla birlikte yedeklenir, taşınır ve kaybolur. Data Protection deposu
yalnızca `*.xml`, anahtar sayımı yalnızca `key-*.xml` okur; ikisi de bu dosyayı
görmez.

Bu bir **yapılandırma dosyası değildir**: `IConfiguration` üzerinden okunmaz,
uygulamayla birlikte seyahat etmez, ve içinde parola yoktur, parolanın şifreli
metni vardır. `CredentialSourceGuard` değişmedi ve hâlâ aynı kuralı uygular.

### Kaynak sırası

1. Korumalı dosya (varsa — kurulumun kendisi yazdı, bu makinedeki en son bilinçli
   eylem odur).
2. Yapılandırma: `Database:*`, parola user-secrets ya da ortam değişkeninden.
   Canlı sunucunun ve geliştirme ortamının yolu **aynen** korunur.
3. İkisi de yoksa **kurulum modu**.

Var olan ama çözülemeyen bir dosya bir sonraki kaynağa düşülerek atlanmaz:
servis durur ve dosyayı adlandırır. Sessizce geri düşmek, ürünü kurulumun
seçtiğinden başka bir veritabanına bağlamak olurdu.

### Kurulum ağa açık olamaz

**Gerekçe: setup cannot be open to the network.** Kurulum sayfası anonimdir —
henüz karşısında oturum açılacak bir hesap tablosu yoktur (ADR-0014) — ve bir
veritabanı yöneticisinin kimlik bilgisini kabul eder. Ağa açık bir anonim uç,
aynı ağdaki herkese "veritabanı yöneticinizin parolasını buraya yazın" diyen bir
form olurdu; Grafana ve pgAdmin'in kapattığı boşluk budur.

Bu yüzden kurulum modunda:

- Kestrel **yalnızca** 127.0.0.1 ve ::1'e, normal kurulumun kullanacağı portta
  bağlanır. `Kestrel:Endpoints` ve `urls`/`ASPNETCORE_URLS` yok sayılır; barındırma
  URL'leri kazanamaz. Başka dinleyici yoktur.
- Açıldıktan sonra bağlanılan adresler geri okunur; loopback olmayan biri varsa
  servis durur.
- Uzak adresi loopback olmayan her istek 403 ile reddedilir.
- `/setup` ve `/api/setup` dışındaki her şey 503 "setup required" döner.

Ürün Windows Server üzerinde bir Windows servisi olarak kurulur; orada yerel bir
tarayıcı (konsolda ya da RDP üzerinden) her zaman vardır. **Headless kurulum
kapsam dışıdır** ve yol haritasında bir tavan olarak kayıtlıdır.

Kurulum tamamlanınca normal bağlama (appsettings'teki adres) devralır. Kurulum
host'u durur ve normal host aynı süreçte sıfırdan kurulur; yanıt yine de
"yanıt gelmezse servisi yeniden başlatın" der — sessiz kalmaz.

### Bir kerelik yönetici kimlik bilgisi

"Veritabanını ve rolü oluştur" yolu bir yönetici kimlik bilgisi alır ve onu
**yalnızca o tek istek içinde** kullanır: hiçbir yere yazılmaz, loglanmaz,
yanıtta ya da hata mesajında geri dönmez, bir istisnanın mesajına girmez.
Bağlantıları havuzsuz açılır, böylece hiçbir havuz o bağlantı dizgisini istekten
sonra yaşatmaz. Sürücünün mesajları `PostgresProvisioning.Explain`'den geçer;
içinde herhangi bir sır geçen cümle bütünüyle geri tutulur.

Kurulum yöneticisinin süper kullanıcı olması gerekmez:

> The setup admin needs CREATEDB and CREATEROLE, not superuser; on PostgreSQL
> 16+ the product role owns the database, and setup achieves that by taking SET
> on it temporarily.

Sıra: `CREATE ROLE` → `GRANT <rol> TO CURRENT_USER WITH SET TRUE` →
`CREATE DATABASE … OWNER <rol>` → `REVOKE`. Yönetici ürün rolünde kalıcı bir
üyelik tutmaz. PostgreSQL 15'ten beri `public` şeması herkese CREATE vermediği
için ürün rolünün veritabanının **sahibi** olması gerekir; ek bir yetki
verilmez.

Ürün rolü hiçbir zaman `SUPERUSER`, `CREATEROLE` ya da `CREATEDB` değildir
(ADR-0016 §4). Parolası sunucuda üretilir (32 rastgele bayt, base64url), kimseye
gösterilmez, ve DDL'e yalnızca SCRAM-SHA-256 doğrulayıcısı olarak girer —
`log_statement = 'ddl'` açık bir sunucunun loguna parolanın kendisi düşmez.

Bir DBA'nın önceden oluşturduğu veritabanı ve rol de kullanılabilir (yönetici
kimlik bilgisi olmadan): rol yukarıdaki üç yetkiden birini taşıyorsa, ya da
şemasında tablo oluşturamıyorsa, kurulum DBA'ya iletilebilecek bir cümleyle
reddeder.

Her iki yolda da başarı ilan edilmeden önce dosya yazılır, geri okunur ve
okunan parolayla yeni bir bağlantı açılarak kanıtlanır. Oluşturma yolu başarısız
olursa oluşturduğunu geri siler.

### Veritabanı kartı

Connections ekranındaki Veritabanı kartı host/port/veritabanı/rolü gösterir,
**parolayı asla**. "Bağlantıyı test et" ve "parolayı döndür" kurulum modunun
dışındadır ve ADR-0014'ün olağan kurallarıyla bir **Administrator** oturumu
ister. Döndürme yönetici kimlik bilgisi istemez (`ALTER ROLE CURRENT_USER`);
yalnızca kaynak korumalı dosyaysa sunulur. Parola yapılandırmadan geliyorsa
reddedilir: ürün oraya yazamaz, ve yalnız sunucuda değiştirmek servisi bir
sonraki açılışta kilitlerdi.

Döndürme sırası: yeni dosya `.pending` adıyla yazılır, sunucuda parola
değiştirilir, dosya yerine taşınır, yeni bağlantıyla doğrulanır. Kalan tek
pencere — sunucu değişikliğiyle taşıma arasında sürecin ölmesi — yeni parolayı
`database-connection.json.pending` içinde bırakır.

### Kabul edilen sınır

Makine kapsamlı DPAPI, dosyanın makineden **çıkmasını** korur (yedek, kopyalanan
klasör, geri yüklenen VM). Aynı makinedeki başka bir yöneticiye karşı korumaz;
bu ADR-0015'te vCenter parolaları için söylenenin aynısıdır.
