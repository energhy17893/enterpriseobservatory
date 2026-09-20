# ADR-0023: PostgreSQL bağımlılığının gerekçesi yeniden kuruluyor

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-21
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (kurulum basitliği), ADR-0011 (SQLite — geçersiz kılınmış),
  ADR-0012 (zaman serisi), ADR-0016 (**§5'in gerekçesini geçersiz kılar**),
  ADR-0017 (saklama pencereleri), ADR-0019 (boş seri satırları), README ilke 1

## Bağlam

Bu ADR bir gözlemden doğdu: **bir bağımlılık, birkaç saat sonra iptal edilen bir
şey için satın alındı ve fatura hiç yeniden okunmadı.**

### Ne satın alındı

ADR-0001, ürünün ayırt edici özelliğini teknik bir detay olarak değil, ürünün
kendisi olarak tarif etti:

> "ürünün en güçlü satış argümanı **tek tıkla kurulan, ~120 MB RAM kullanan, ek
> appliance gerektirmeyen Windows MSI paketi** olması. Ana rakip (VMware Aria
> Operations) 32+ GB RAM isteyen bir vApp olarak geliyor. Kurulum basitliği
> teknik bir detay değil, ürünün kendisi."

ADR-0016 (2026-09-20) bu kısıtı bilerek kaldırdı ve bedelini açıkça yazdı:
"Kurulum artık iki adımdır", "Ürün, veritabanı olmadan başlamaz", "Yedekleme
sorumluluğu müşteriye geçer", ve "ürünü kimin satın alabileceğini daraltır".

Bedeli neyin haklı çıkardığı §1'de iki bacaklı olarak duruyor:

> "Bugün gerekçesini savunan tek bileşen PostgreSQL'dir: **kalıcı ölçüm geçmişi
> ve eşzamanlı okuma/yazma**, bir dosyanın rahat taşıyabileceğinin ötesinde."

Birinci bacak — kalıcı geçmiş — §"Bağlam"da da başa konmuştu: "Kapasite planlama
ve eğilim çıkarımı **yıllarla ölçülen geçmiş** ister." Ve §5 bunu somut bir
saklama vaadine bağladı:

> "'Kalıcı' için ayrı bir çözünürlük katmanı **eklenmez**: ADR-0012'nin beş
> istatistiği kayıpsız yeniden toplandığı için, **saatlik katmanı uzun tutmak
> yeterlidir.** 200 varlık × 8 sayaç × saatlik ≈ yılda 14M satır — PostgreSQL
> için önemsiz."

### Ne iptal edildi

ADR-0017, **aynı gün** (2026-09-20), ölçüme dayanarak saatlik kademeyi 400
günden 90 güne indirdi ve kaybı kendi başlığı altında yazdı:

> "**Yıla yıl karşılaştırma bitti.** ADR-0012 400 günü tam olarak bunun için
> seçmişti… 90 günde bu soru **cevaplanamaz.**"

Yani ADR-0016 §5'in "saatlik katmanı uzun tutmak yeterlidir" cümlesi, o cümleyi
taşıyan ADR kabul edildikten saatler sonra geçersizleşti. ADR-0017 ADR-0016'yı
yalnızca **"İlgili: ADR-0016 (PostgreSQL bağımlılığı)"** diye anıyor; ADR-0012
için "kısmen geçersiz kılar" dediği hâlde ADR-0016 §5 için aynı şeyi demiyor.

Sonuç, kayıtta bir boşluk: dış altyapı bağımlılığının yazılı gerekçesinin bir
bacağı artık ürün davranışına karşılık gelmiyor, ve bunu kayıttan okuyan biri
farkı göremiyor.

### Denetlenen aritmetik

ADR-0016 §5 ve ADR-0017'nin sayıları bu ADR için yeniden hesaplandı. İkisinde de
hata var, ve ikisi de aynı yöne — bağımlılığın lehine — sapıyor.

**1. §5'in 14M satırı, kendi içinde tutarlı ama yanlış seri sayısıyla kurulmuş.**
200 × 8 = 1 600 seri, × 8 760 saat/yıl = 14,0M satır/yıl; hesap doğru. Ama
ADR-0017'nin ölçtüğü estate **aynı 200 varlıkta 6 084 seri** taşıyor (varlık
başına ~30 seri, ~8 değil). Gerçek sayı:

```
6 084 seri × 8 760 saat/yıl              = 53,3 M satır/yıl
53,3 M × 173,1 bayt/bucket (ölçüm)       = 9,2 GB/yıl
```

§5 **3,8 kat** eksik tahmin etmiş. "PostgreSQL için önemsiz" cümlesi 14M için
savunulabilirdi; yılda 9,2 GB için savunulamaz — ve zaten ADR-0017'nin tam da bu
yüzden kestiği miktar bu.

**2. ADR-0017'nin "kırk kat ucuz" ifadesi yanlış; doğru çarpan 24.**
ADR-0017 alternatif C'yi tarif ederken şöyle diyor: "Günlük kova, seri başına
yılda 365 satırdır — 400 günlük saatlikten **kırk kat** ucuz." Aynı cümle
`product-architecture.md` §8'de de tekrarlanıyor. ADR-0017'nin kendi tablosundan:

```
400 gün saatlik   24/gün × 400 gün = 9 600 satır/seri
400 gün günlük     1/gün × 400 gün =   400 satır/seri
                                       ───────────────
                                       9 600 / 400 = 24
```

Yıl bazında da aynı: 8 760 / 365 = 24. Çarpan, bir gündeki saat sayısıdır ve
40 olamaz. Sonucu değiştirmiyor — 24 kat da çok ucuz — ama ölçümle yazıldığını
söyleyen bir ADR'de türetilemeyen bir sayının durması, ADR-0012'nin "neredeyse
bedava" cümlesiyle aynı cinsten bir hatadır.

**3. Günlük kademenin gerçek maliyeti (türetme açık).**
Girdiler: 6 084 seri (ADR-0017), bucket satırı 173,1 bayt
(`docs/live-verification.md` §7), günlük kova = 365 satır/seri/yıl.

```
1 yıl:  6 084 × 365   =  2,22 M satır × 173,1 B =   0,36 GB
3 yıl:  6 084 × 1 095 =  6,66 M satır × 173,1 B =   1,07 GB
5 yıl:  6 084 × 1 825 = 11,10 M satır × 173,1 B =   1,79 GB
```

Karşılaştırma ölçeği, ADR-0017'nin kendi kestiği miktar: 20,9 − 13,6 = **7,3 GB**.
(Doğrulandı: (9 600 − 2 160) × 6 084 × 173,1 B = 7,30 GB — ADR-0017'nin rakamı
tutuyor.)

Yani **beş yıllık günlük geçmiş 1,79 GB'dır** ve ADR-0017'nin 13 aylık saatlik
kuyruk için ödediği 7,3 GB'ın **dörtte birinden azdır.** Kesilen şey pahalıydı;
kesilenin karşıladığı ihtiyaç ucuz.

### Denetlenen iddialar — doğru çıkmayanlar

Bu incelemeyi tetikleyen okumanın iki iddiası belgelerde karşılık bulmadı ve
kayda geçirilmesi gerekiyor:

- **`docs/reference-approaches.md` §2 SQLite etrafında yazılmış değildir.** O
  bölüm saklama kademelerini vROps ile karşılaştırıyor, ADR-0017'nin 90 gününü
  zaten içeriyor ve içinde SQLite geçmiyor. Kullanılabilir önceki akıl yürütme
  taşıyor, ama saklama hakkında — depolama motoru hakkında değil.
- **ADR-0016'nın tamamı iptal edilmedi.** §1'in ikinci bacağı ("eşzamanlı
  okuma/yazma") ADR-0017'den etkilenmiyor, ve §"Bağlam"ın ikinci gerekçesi
  (olay/log ekseni) da ayakta. İptal olan §5 ve §"Bağlam"ın birinci maddesidir.
  Bu ADR bağımlılığı bütünüyle sorgulamıyor; **gerekçesinin hangi bacağının
  taşıdığını** sorguluyor.

## Karar

### 1. ADR-0016 §5 artık bağımlılığı taşımıyor

ADR-0016'nın durum satırına "§5 ADR-0023 ile geçersiz kılındı" eklenir.
PostgreSQL bağımlılığının gerekçesi olarak **saklama süresi bir daha
gösterilmez.** ADR-0017'den sonra ürünün saatlik ufku 90 gündür ve bu, bir
dosyanın rahatça taşıyabileceği bir hacimdir — kesilen 7,3 GB tam olarak bunun
kanıtıdır.

### 2. Bağımlılık kalır; gerekçesi yazma yoluna taşınır

PostgreSQL sürdürülür. Ama gerekçe ADR-0016 §1'in **ikinci** bacağına, eşzamanlı
okuma/yazmaya taşınır ve artık ölçülmüş değil, **kullanılmakta olan** şeylere
dayandırılır. Bugün `Persistence.Postgres` içinde çalışan ve bir dosya motorunda
karşılığı olmayan şeyler:

| Kullanılan | Nerede | Ne için |
|---|---|---|
| İkili `COPY` (`BeginBinaryImport`) | 5 çağrı yeri: graf deposu (4), ölçüm deposu (1) | Uzlaştırma ve örnek yazma yolunun toplu yazması |
| `unnest(@dizi, …)` ile dizi bağlama | `PostgresObservationStore` | Seri bağlamayı tek gidiş-dönüşte yapmak |
| `FOR KEY SHARE` | Örnek ekleme yolu | Yazarken seri satırının altından çekilmesini engellemek |
| `FOR UPDATE SKIP LOCKED` | Süpürme yolu | Süpürmenin sıcak yolu kilitlememesi |
| `FOR UPDATE` | `PostgresUserAccountStore` | Okuma-değiştirme-yazma yarışı |

Bunlar süs değil: ADR-0019, tam da bu kilit tasarımı olmadan ortaya çıkan bir
yarışın (`sample.series_id` CASCADE'i ile sessiz örnek kaybı) kaydıdır.

Buna işletimsel aşinalık ve ADR-0001'in açık bıraktığı çok-prosesli topoloji de
eklenir — ama **ikisi de bugün ölçülmemiş beklentilerdir** ve bu ADR onları
gerekçenin taşıyıcısı olarak saymaz. Taşıyan şey yukarıdaki tablodur.

### 3. Kalıcı geçmiş vaadi boş bırakılmaz: günlük dördüncü kademe eklenecek

ADR-0016 kalıcı geçmiş için bir bağımlılık aldı; ADR-0017 o geçmişi kesti.
Üçüncü bir durum kabul edilmiyor — vaat ya geri alınır ya karşılanır. Karşılanır:

**ADR-0017'nin alternatif C'si (günlük dördüncü kademe) benimsenir.** Varsayılan
saklama süresi bu ADR'de belirlenmez; kademenin varlığı belirlenir. Tasarımı —
katlama sırası, su işareti, testler — ADR-0012'nin mevcut kurallarını izler ve
ayrı bir iş kalemidir.

Gerekçe yukarıdaki türetmedir: beş yıllık günlük geçmiş 1,79 GB'dır, kesilen
7,3 GB'ın dörtte birinden azdır ve mevsimselliği görmeye fazlasıyla yeter.
ADR-0017 bunu "muhtemelen bir gün yapılacak" diye erteledi; ertelemenin gerekçesi
"mevsimsellik ihtiyacı henüz somut bir talep değil" idi. O gerekçe artık
geçersiz: talep somut olmayabilir, ama **bir altyapı bağımlılığının yazılı
gerekçesi** somuttur ve bugün karşılıksızdır.

### 4. Kayıttaki sapmalar düzeltilir

`product-architecture.md` §8 hâlâ "PostgreSQL neden?" diye soruyor ve "Gerekçe
netleşmeden adaptör yazmak, yanlış problemi çözmek olur" diyor — oysa ADR-0016
gerekçeyi verdi ve adaptör yazıldı (11 dosya, 3 454 satır, 23 tablo). `README.md`
hâlâ "SQLite seçimi bir dağıtım kararı olarak kaldığı sürece" diyor. İkisi de
düzeltilir; bu ADR'den ayrı commit'lerde, bağımsız okunabilsinler diye.

## Gerekçe

**Bir bağımlılığın gerekçesi, bağımlılığın kendisi kadar bakıma muhtaçtır.**
ADR-0016 kendi kuralını koydu: "Her bağımlılık kendi değerini savunmak
zorundadır." Bir savunmanın yarısı çökerse, kural onu kendiliğinden askıya
almaz — birinin bakıp yeniden yazması gerekir. Bu ADR o bakmadır.

**Bağımlılığı geri almamanın sebebi konfor değil, ölçülmüş kod.** ADR-0017
saklama gerekçesini çürüttü ama yazma yolu gerekçesini çürütmedi; tam tersine,
o gün ölçülen 335 örnek/saniyelik yazma hızı ve ADR-0019'un kaydettiği süpürme
yarışı, ikinci bacağın gerçek olduğunu gösteriyor.

**Vaadin boş kalması, iptal edilmesinden kötüdür.** "Kalıcı geçmiş için
PostgreSQL aldık" cümlesi kayıtta dururken ürünün ufkunun 90 gün olması, altı ay
sonra bu ADR'leri okuyan birinin çıkaramayacağı bir durumdur. README ilke 1
("asla uydurma") ölçüm verisi için konmuştu; kendi kayıtlarımız için de geçerli
olmalı.

## Değerlendirilen alternatifler

### A. Gerekçeyi geri getir — günlük dördüncü kademe

ADR-0017'nin kendi alternatif C'si. Yıla yıl karşılaştırmayı geri verir, maliyeti
yukarıda türetildi (5 yıl = 1,79 GB), ve ADR-0012'nin beş sayılı kovası kayıpsız
yeniden toplandığı için saatlikten günlüğe indirgemek veri bozmaz.

**Tek başına seçilmedi** çünkü bağımlılığı tek başına haklı çıkarmıyor: 1,79 GB'lık
bir kademe, bir dosya motorunun da rahatça taşıyacağı bir yüktür. Yani günlük
kademe, kalıcı geçmiş *vaadini* kurtarır ama *PostgreSQL gerekçesini* kurtarmaz.
Bu ayrım önemli ve ADR-0016'nın ilk hâlinde yapılmamıştı.

Kararın 3. maddesi olarak benimsendi — gerekçe olarak değil, ödenmemiş bir borç
olarak.

### B. PostgreSQL'i koru, gerekçeyi başka temele oturt

Karar 2'de seçilen yol. Avantajı: gerekçe, tahmin edilen bir gelecek hacme değil,
bugün çalışan koda dayanır ve bir sonraki ölçümde çürüyemez.

Dürüst olmak gerekirse bu, ADR-0016'nın §1'de zaten söylediğinin öne alınmasıdır
— yeni bir keşif değil, **sıralamanın düzeltilmesi.** ADR-0016 hacmi başa,
eşzamanlılığı sonuna koydu; ölçüm ikisinin yerini değiştirdi.

### C. Bağımlılığı yeniden düşün — tek dosyalık motora dönmek

Ciddiye alındı, çünkü ADR-0017'den sonra hacim gerekçesi gerçekten kalmadı:
13,6 GB'lık bir kararlı durum SQLite için de erişilebilirdir, ve ADR-0001'in
"tek tıkla kurulum" argümanı ürünün en güçlü satış argümanı olarak duruyor.

Somut yeniden yazma yüzeyi bugün şudur:

- `src/EnterpriseObservatory.Persistence.Postgres/`: **11 dosya, 3 454 satır** —
  7 store, şema (23 tablo), `PostgresDatabase`, `PgValues`.
- `tests/EnterpriseObservatory.Persistence.Postgres.Tests/`: **2 159 satır,
  63 test metodu**, bunların **52'si canlı bir PostgreSQL isteyen
  `[SkippableFact]`.** Bu 52'si motoru değiştiren bir kararda yeniden yazılır;
  kalan 11 saf `[Fact]` (seri bağlama, katlama sırası) motordan bağımsızdır ve
  kalır.
- Karar 2'nin tablosundaki beş mekanizma yeniden çözülür. En pahalısı kilitler:
  tek yazarlı bir dosya motorunda `FOR KEY SHARE`/`SKIP LOCKED` yoktur, yani
  ADR-0019'un kaydettiği süpürme-ekleme yarışı **yeniden ve farklı biçimde**
  çözülmek zorundadır. Bu, satır sayısıyla ölçülemeyen kısımdır.
- Çekirdek **değişmez.** `IObservationStore` ve kardeşleri Application
  katmanında port; mimari test somut motoru yalnızca `Host`'un görmesini
  zorluyor. Bu, ADR-0001'in asıl iddiasıydı ve burada ikinci kez doğrulanıyor:
  motor değiştirmek 5 600 satırlık bir adaptör+test işidir, mimari işi değil.

**Seçilmedi**, ve sebebi hacim değil:

1. **ADR-0016'nın kendi red gerekçesi ayakta.** "SQLite'ta kalıp saklamayı
   uzatmak" reddedilirken ileri sürülen şey saklama değil, olay/log ekseniydi:
   "aynı karar yeniden verilecekti, ve o noktada taşınacak veri olacaktı." Bu
   argümana ADR-0017 dokunmadı. Bugün geri dönmek, ADR-0016'nın sildiği taşıma
   borcunu geri yaratır — ve bu sefer sahada veri olabilir.
2. **Çift göç, tek yönlü göçten pahalıdır.** ADR-0016 "göç yolu yok" borcunu
   sahada kurulum olmadığı için kabul etti. Bugün geri dönmek aynı borcu ikinci
   kez, aynı gerekçeyle doğurur.
3. **Yazma yolu gerekçesi gerçek.** Karar 2'nin tablosu tahmin değil, çalışan
   kod. Geri dönüş, çözülmüş bir eşzamanlılık problemini yeniden açar.

**Kabul edilen bedel — açıkça:** ADR-0001'in "tek tıkla kurulum" argümanı geri
alınmıyor, ve ADR-0016'nın "ürünü kimin satın alabileceğini daraltır" cümlesi
yürürlükte kalıyor. Bu ADR o bedeli azaltmıyor; yalnızca **karşılığında ne
alındığını** doğru yazıyor.

### D. Hiçbir şey yapma — ADR-0016'yı olduğu gibi bırak

Seçilmedi. §5 bugün yanlış bir cümle taşıyor ve bu cümle, kaydın tamamının
güvenilirliğini düşürür. ADR-0002'nin dediği gibi ADR'lerin amacı altı ay sonraki
okuyucudur; o okuyucuya iptal edilmiş bir gerekçeyi yürürlükteymiş gibi sunmak,
ADR tutmamaktan farksızdır.

## Sonuçlar

### Olumlu

- Bağımlılığın yazılı gerekçesi, ölçümle çürütülemeyecek bir temele oturur:
  çalışan kod.
- "Kalıcı ölçüm geçmişi" bir vaat olmaktan çıkıp planlanmış bir kademe olur.
- ADR-0016 → ADR-0017 arasındaki boşluk kapanır; ikisi arka arkaya okunduğunda
  çelişki kalmaz.
- ADR-0017'deki "kırk kat" ve ADR-0016 §5'teki 14M satır, kayıtta düzeltilmiş
  olarak durur.

### Olumsuz / kabul ettiğimiz bedel

- **Günlük kademe bir iş kalemidir ve bu ADR onu bitirmiyor.** Yeni bir katlama
  yolu, yeni bir su işareti, yeni testler — ADR-0017'nin erteleme gerekçesi
  (maliyet) hâlâ doğru; değişen, ertelemeye değip değmediği.
- **Bağımlılığın gerekçesi daraldı.** Artık "yıllarca geçmiş" gibi geniş bir
  başlık yok; yerine beş somut mekanizma var. Bu mekanizmalar bir gün
  gereksizleşirse — örneğin yazma hacmi düşerse — bağımlılık yeniden savunmasız
  kalır. Bu bilinçlidir: dar bir gerekçe, geniş ve yanlış bir gerekçeden iyidir.
- **ADR-0016 §5 iptal edilirken §1 ve §2 korunuyor**, yani ADR bütün olarak
  okunamaz hâle geliyor; okuyucunun durum satırını görmesi gerekiyor. Alternatifi
  ADR-0016'yı tümden geçersiz kılıp yeniden yazmaktı ve bu, doğru olan kısmı da
  tarihe gömerdi.
- Bu ADR bir **kayıt düzeltmesidir**; ürün davranışını bugün değiştirmiyor.
  Karar 3 yapılana kadar ürünün ufku 90 gündür.

### Bu kararı yeniden değerlendirmemiz gereken durum

- **Karar 2'nin tablosu boşalırsa.** İkili `COPY` ve satır kilitleri ölçülebilir
  bir fayda vermiyorsa, bağımlılığın gerekçesi yine yoktur ve alternatif C
  yeniden açılır.
- **Günlük kademe (karar 3) yapılmadan bir sürüm çıkarsa**: o noktada dürüst
  hareket, kademeyi eklemek değil, ADR-0016'nın "kalıcı geçmiş" vaadini kayıttan
  **geri çekmektir.** İkisinden biri.
- **Olay/log ekseni kendi motorunu (ADR-0016'daki Elasticsearch ihtimali)
  getirirse**: o zaman "iki arka uç maliyeti" argümanı zaten ödenmiş olur ve
  PostgreSQL'in tek motor olma gerekçesi yeniden ölçülmelidir.
- **Tek kurulumda 6 084'ün belirgin biçimde üstünde seri sayısı ölçülürse**: bu
  ADR'nin bütün türetmeleri 6 084 seri içindir ve rakamla doğrusal büyür.
