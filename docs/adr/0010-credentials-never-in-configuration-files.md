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
