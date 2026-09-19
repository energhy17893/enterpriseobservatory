# ADR-0016: Ürünün dış altyapı bağımlılıkları olur

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (§"ek appliance gerektirmeyen MSI" iddiasını değiştirir),
  ADR-0011 (**geçersiz kılar**), ADR-0012 (zaman serisi), ADR-0010 (kimlik
  bilgileri), README ilke 1

## Bağlam

ADR-0001, ürünün ayırt edici özelliği olarak **"ek appliance gerektirmeyen
MSI"** dedi: rakip 32+ GB RAM isteyen bir sanal makine dayatırken, bu ürün tek
tıkla kurulacaktı. ADR-0011 gömülü SQLite'ı tam bu gerekçeyle seçti — "dosya
olan bir veritabanı servis, port ve DBA istemez".

O gerekçe geçerliydi. Değişen, ürünün ne yapmayı taahhüt ettiği.

### Ürün kapsamı, kurgunun taşıyabileceğinin ötesine geçti

`docs/product-architecture.md` ürünün dört soruyu cevapladığını tarif ediyor:
sağlıklı mı, doğru kurulmuş mu, neden yavaş, sırada ne bozulacak. Bunlardan
ikisi, gömülü bir dosyanın rahat taşıyamayacağı şeyler istiyor:

- **Kalıcı ölçüm geçmişi.** Kapasite planlama ve eğilim çıkarımı yıllarla
  ölçülen geçmiş ister. Bugünkü saklama (ham 2 gün / 5-dakika 30 gün / saatlik
  400 gün) mimari bir karar değil, SQLite seçiminin yan etkisiydi.
- **Olay ve log ekseni.** Alt sistemlerin kayıtlarını karşılıklı analiz etmek,
  ileride tam metin arama ve çok daha yüksek hacim demek.

### Önceki üründen gelen kanıt

Önceki ürün geçmişi PostgreSQL'de tutuyordu — daha doğrusu, **tutmayı
amaçlıyordu.** Canlı veritabanı 2026-09-20'de ölçüldü:

| Tablo | Satır |
|---|---|
| `historical_metrics` | **0** |
| `monitoring_snapshots` | **0** |
| `vcenter_events` | 5.475 (hepsi tek gün, hepsi `Info`, **%95'i nesneye bağlanamamış**) |
| `alert_instances` | 801 (JSONB blob) |
| **toplam boyut** | **13 MB** |

Üstelik `historical_metrics` şemasında **varlık sütunu yoktu**
(`metric_name, metric_value, unit, source`), yani dolu olsaydı bile "*bu
host'un* disk gecikmesi" sorusunu cevaplayamazdı.

Bu, iki yönde birden bilgi veriyor: taşınacak veri **yok**, eşleşilecek bir
çözünürlük **yok**; ve "Postgres'te kalıcı geçmiş" hiç çalışmamış bir vaatti.
Yani bu karar bir miras kısıtını sürdürmek değil, ilk kez tasarlamak.

## Karar

### 1. "Appliance gerektirmez" bir kısıt olmaktan çıkar

Dikkat edilecek nokta: bu, katı bir kuralı tersine çevirmek **değildir.**
"Appliance yasak" yerine "appliance zorunlu" koymak aynı katılığın öbür yüzü
olurdu.

Kural şu hâle gelir:

> Ürün, çevresel bir bileşene **bileşen bazında ve gerekçeyle** bağımlı olur.
> Her bağımlılık kendi değerini savunmak zorundadır; hiçbiri "artık altyapı
> kullanıyoruz" gerekçesiyle gelemez.

Bugün gerekçesini savunan tek bileşen PostgreSQL'dir: kalıcı ölçüm geçmişi ve
eşzamanlı okuma/yazma, bir dosyanın rahat taşıyabileceğinin ötesinde.
Elasticsearch **adı geçen bir ihtimaldir, karar değildir** — sırası geldiğinde
kendi gerekçesini ve kendi ADR'sini getirecektir.

Bu, vSphere istatistik seviyesi 2 önkoşuluyla aynı sınıftır: ürün platformdan
bir şey ister ve bunu peşinen söyler. Ama bir farkı vardır ve saklanmamalıdır —
istatistik seviyesi, müşterinin **zaten sahip olduğu** bir sistemdeki bir
ayardır; PostgreSQL ise **kurulacak, işletilecek, yedeklenecek ve yamalanacak**
yeni bir altyapıdır. Bu, ürünü kimin satın alabileceğini daraltır ve kabul
edilen bedeldir.

Karşılığında ADR-0001'in asıl rekabet argümanı korunur: rakip 32+ GB RAM'lik
bir appliance dayatırken, bu ürün bir MSI ve yanında sıradan bir PostgreSQL
ister. Fark hâlâ büyüktür; mutlak değil, orantılıdır.

### 2. PostgreSQL tek depolama motorudur

ADR-0011 geçersiz kılınır. SQLite kaldırılır — durum için de, ölçüm için de.

İki arka ucu birden desteklemek cazipti ve reddedildi: iki şema, iki göç yolu,
iki test matrisi, **kalıcı olarak**. Tek geliştiricili bir üründe bu maliyet,
küçük kurulumlara sağlanan kolaylıktan büyüktür.

### 3. Çekirdek değişmez

Bu bir **dağıtım** kararıdır, mimari değişiklik değil — ve ADR-0001'in asıl
ilkesi ("deployment-agnostik çekirdek") tam da bunu mümkün kıldığı için
ayakta kalır.

`IObservationStore` Application katmanında bir porttur ve mimari test, somut
depolama motorunu yalnızca `Host`'un görebildiğini zorlar. Postgres adaptörü
eklemek Domain, Application veya Api'de **tek satır** değiştirmedi. Aynı
mekanizma, ileride olay eksenine Elasticsearch takmayı da aynı ölçüde ucuz
kılar: yeni bir port, yeni bir adaptör, çekirdekte değişiklik yok.

> Bu ADR, ADR-0001'in mimarisini doğrulayan bir olaydır, onu çürüten değil.
> Dağıtım vaadi değişti; tasarım değişmedi.

### 4. Bağlantı bilgisi bir kimlik bilgisidir

Bağlantı dizgisi parola taşır. ADR-0010 aynen geçerlidir: parola bir
`Secret`'tır, ayar dosyasında bulunamaz, ve `CredentialSourceGuard` bunu
kapsamak zorundadır.

Ürün **süper kullanıcıyla bağlanmaz.** Kendi rolü, kendi veritabanının sahibi,
`superuser=false createdb=false createrole=false`. İlke 5 ("izleme aracı
üretimi bozmaz") bir veritabanı sunucusu için de geçerlidir: izleme aracı,
sunucudaki her şeyi silebilecek bir hesapla çalışmamalıdır.

### 5. Saklama artık bir ürün kararıdır

Saklama süreleri SQLite'ın dayattığı sınır olmaktan çıkar ve
`SeriesRetentionPolicy` üzerinden açıkça yapılandırılır. "Kalıcı" için ayrı bir
çözünürlük katmanı **eklenmez**: ADR-0012'nin beş istatistiği kayıpsız yeniden
toplandığı için, saatlik katmanı uzun tutmak yeterlidir. 200 varlık × 8 sayaç ×
saatlik ≈ yılda 14M satır — PostgreSQL için önemsiz.

## Sonuçlar

### Kurulum artık iki adımdır

"Bir MSI çalıştır, bitti" bitti. Yerine: PostgreSQL kur, bir veritabanı ve rol
oluştur, MSI'ı çalıştır, bağlantıyı yapılandır. Bu, ürünün ilk deneyimini
uzatır ve dürüstçe belgelenmelidir.

### Ürün, veritabanı olmadan başlamaz

Bu bilinçlidir ve ilke 1'in bir uygulamasıdır: depolayamayan bir izleme aracı,
yarı çalışarak devam etmektense durmalıdır. Başlamama mesajı, eksik olanın ne
olduğunu ve nasıl sağlanacağını söylemek zorundadır.

### Yedekleme sorumluluğu müşteriye geçer

SQLite'ta veritabanı, servisin yanındaki bir dosyaydı ve MSI'ın yedeklenmesi
yeterliydi. Artık PostgreSQL'in kendi yedekleme prosedürü gerekiyor — ve
ADR-0015'in anahtar zinciri uyarısı burada da geçerli: **anahtar zinciri ile
veritabanı aynı yedeğe konmamalıdır.**

### Kabul edilen borç

- **Göç yolu yok.** SQLite'ta veri tutan bir kurulumun PostgreSQL'e taşınması
  için araç yazılmadı. Bugün sahada böyle bir kurulum olmadığı için kabul
  edildi; olursa bu borç ödenmelidir.
- **Bölümleme (partitioning) yok.** Yıllarca biriken `sample` ve `bucket`
  tabloları eninde sonunda tarih bazlı bölümleme isteyecek. Bugünkü hacimde
  gerekmiyor; gerektiğinde ölçülerek eklenir, tahminle değil.
- **Elasticsearch bir niyet, karar değil.** Olay/log ekseni için adı geçiyor
  ama portu yazılmadı, gerekçesi ölçülmedi. Sırası geldiğinde kendi ADR'sini
  gerektirir — `product-architecture.md` §5'in "ham log değil, yapılandırılmış
  olay" önerisi hâlâ açık bir sorudur.

## Reddedilen alternatifler

**Opsiyonel arka uç (SQLite varsayılan, Postgres isteyene).** İlk önerilen
buydu ve reddedildi: iki motoru süresiz desteklemenin maliyeti, küçük
kurulumlara sağladığı kolaylıktan büyük. Tek kod yolu, tek operasyon hikâyesi.

**SQLite'ta kalıp saklamayı uzatmak.** Saatlik katman yıllarca tutulabilirdi.
Ama olay/log ekseni geldiğinde aynı karar yeniden verilecekti, ve o noktada
taşınacak veri olacaktı. Kararı erken vermek, taşıma borcunu doğmadan siliyor.

**Zaman serisi veritabanı (InfluxDB, TimescaleDB, Prometheus).** Ölçüm için
daha iyi olurdu; ama durum, alarm, hesap ve yapılandırma için ikinci bir motor
gerektirirdi — yani reddedilen "iki arka uç" maliyetine geri dönerdi.
PostgreSQL ikisini birden yeterince iyi yapar, ve TimescaleDB gerekirse aynı
motorun uzantısı olarak sonradan gelebilir.
