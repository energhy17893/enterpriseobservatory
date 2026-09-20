# ADR-0022: PostgreSQL testleri ayrı bir Linux işinde, tek kullanımlık bir parolayla çalışır

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-21
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (uyarılar hata sayılır), ADR-0010 (kimlik bilgileri
  yapılandırmada durmaz), ADR-0016 (dış altyapı bağımlılıkları),
  ADR-0020 (anahtar halkası dayanıklılığı açılışta denetlenir),
  `docs/live-verification.md` (kanıt iddiadan önce gelir)

## Bağlam

`dotnet test` yalnızca `windows-latest` üzerindeki tek bir işte çalışıyordu.
O koşucuda ne bir PostgreSQL sunucusu ne de `EO_TEST_PG_PASSWORD` vardı.
`LiveDatabase.SkipReason` parolanın yokluğunu görüp testleri atlıyor — bilerek
ve doğru biçimde, çünkü sunucusu olmayan bir makine paketin geri kalanını
çalıştırabilmeli.

Sonuç ölçüldü: **63 testin 52'si her itmede atlanıyordu.** Geriye kalan 11'i
sunucuya dokunmayan saf birim testleridir.

Bu, depolama katmanının hiçbir yerde sınanmadığı anlamına geliyordu. CI'da
değil; geliştirme makinesinde de değil, çünkü `LiveDatabase` bir port
okumuyordu ve 5432'yi elinde tutan her neyse ona bağlanmak zorundaydı.

Kritik olan, atlanan bir paketin yeşil görünmesidir. 52'sinin de atladığı bir
koşu ile 52'sinin de geçtiği bir koşu dışarıdan **birebir aynı** görünür.
Bu hafta birkaç iş tam olarak bu yanlış güvenceye dayanarak, *"burada
kanıtlanmadı; canlı test CI'da çalışacak"* notuyla girdi. Çalışmadı.

Kapsanmayanların somut listesi: `PostgresObservationStore.Append` içindeki
`SELECT ... FOR KEY SHARE` kilidi gerçekten alıyor mu, `FOR UPDATE SKIP LOCKED`
gerçekten atlıyor mu, kullanıcı hesabı deposundaki `SELECT ... FOR UPDATE`
kilitlenme yarışını kapatıyor mu, ve yeniden başlatma sonrası alarm kapsamı
canlı kopyayla uyuşuyor mu. Bunların hiçbiri sahte bir katmanın cevaplayabileceği
sorular değil; hepsini sunucu karara bağlar.

Kısıt: GitHub Actions `services:` kapsayıcıları yalnızca Linux koşucularında
çalışır. Mevcut iş Windows'tur ve bu bir kaza değildir — anahtar halkası
testleri DPAPI ve `%TEMP%` davranışını sınar (ADR-0020) ve bunlar yalnızca
Windows'ta bir anlam taşır.

## Karar

**PostgreSQL testlerini, mevcut Windows işinin yanında duran ayrı bir
`ubuntu-latest` işinde çalıştır.** Parola işin içinde üretilir, maskelenir ve
koşucuyla birlikte yok olur. İşin son adımı hiçbir testin atlanmadığını
doğrular.

Kapsam: bu ADR yalnızca bu testlerin nerede ve hangi kimlikle çalıştığını
belirler. Testlerin içeriğini, üretim kodunu ve `SkipReason` davranışının
kendisini değiştirmez — yerel makinede atlama hâlâ doğru davranıştır.

Windows işi paketin tümünü çalıştırmaya devam eder ve orada 52 test atlanmaya
devam eder. Bu artık yanıltıcı değildir çünkü aynı testler Linux işinde
atlanamaz hâle gelmiştir.

## Gerekçe

**Parola bir sır değildir ve olmamalıdır.** `openssl rand -hex 32` ile işin
içinde üretilir ve `::add-mask::` ile günlüklerden maskelenir. Koruduğu şey,
yalnızca o koşucunun geri döngü arayüzünde duran ve iş bitince silinen bir
kapsayıcıdır. Bir depo sırrı, korunmaya ve **yeniden kullanılmaya** değer bir
değer olduğunu ima ederdi; bu değer ikisi de değildir. ADR-0010'un ilkesi
burada da geçerlidir: depoya kimlik bilgisi girmez.

**Kapsayıcı `services:` yerine bir adımda başlatılır.** İdiomatik şekil
`services:`'tir ve sağlık denetimini bedavaya verir. Ancak bir servisin ortamı
herhangi bir adım çalışmadan **önce** sabitlenir; yani parolası ya bu dosyada
düz metin bir sabit ya da bir depo sırrı olmak zorundaydı. İkisi de kabul
edilemez olduğu için kapsayıcı `docker run` ile başlatılır ve hazır olması
`pg_isready` döngüsüyle beklenir. Ödenen bedel budur: elle yazılmış bir hazırlık
döngüsü, karşılığında işe özgü ve gerçekten tek kullanımlık bir parola.

**Atlamayı iş başarısızlığı say.** Bu işin var oluş sebebi testlerin çalışmış
olmasıdır; çalışmadıkları hâli yeşil bırakmak, kapatmaya çalıştığımız hatanın
tam olarak kendisini geri getirir. Son adım `trx` sayaçlarını okur ve
`executed < total` ise işi düşürür. Ortam bağlantısı ileride sessizce bozulursa
bu, yeşil bir koşu değil kırmızı bir koşu üretir.

## Değerlendirilen alternatifler

### Alternatif A: Mevcut Windows işine bir PostgreSQL kur
`services:` Windows'ta çalışmaz, dolayısıyla sunucu Chocolatey ile kurulup elle
başlatılmak zorunda kalırdı: daha yavaş, daha kırılgan ve koşucu imajının
sürümüne bağımlı. Windows'a özgü bir depolama davranışı sınasaydık bu alternatif
daha iyi olurdu; sınamıyoruz — SQL'i karara bağlayan sunucudur ve sunucunun
hangi işletim sisteminde durduğu testin konusu değildir.

### Alternatif B: Windows işini Linux'a taşı
Tek bir iş kalırdı ve `services:` kullanılabilirdi. Reddedildi: anahtar halkası
testleri DPAPI ve `%TEMP%` semantiğini sınar (ADR-0020) ve Linux'ta ya atlanır
ya da başka bir şeyi sınarlar. Ürün Windows'a kurulan bir üründür; onu hiç
Windows'ta derlememek daha büyük bir kör nokta açardı.

### Alternatif C: `services:` kullan, parolayı dosyaya sabit yaz
En kısa YAML. Parola gerçekten değersiz olduğu için savunulabilir bir okuma var.
Reddedildi çünkü depodaki sabit bir parola, kopyalanmaya ve "bu zaten hep böyle
yazılıyordu" hâline gelmeye açıktır. Önceki ürün tam olarak böyle bir kimlik
bilgisi sızdırdı. Maliyeti düşük bir disiplin, maliyeti yüksek bir alışkanlığın
önünü kesiyor.

### Alternatif D: `services:` kullan, parolayı bir depo sırrından al
Görev tanımı bunu açıkça dışladı ve gerekçe sağlamdır: bir sır, dönmesi gereken
ve erişimi denetlenen bir varlıktır. Efemeral bir kapsayıcı için sır üretmek,
sır kavramını ucuzlatır ve gerçek sırların arasında gürültü yaratır.

### Alternatif E: Testcontainers kullan
Kapsayıcı yaşam döngüsünü test koduna taşır ve yerelde de çalışır. Cazip, ancak
test projesine yeni bir bağımlılık ve Docker ön koşulu ekler; `LiveDatabase`'in
mevcut ortam değişkeni sözleşmesi zaten çalışıyor ve bu görev üretim dışı da
olsa gereksiz yüzey açmamalı. Yeniden değerlendirmeye değer, bkz. aşağısı.

## Sonuçlar

### Olumlu
- 52 test artık bir yerde çalışıyor. Ölçüldü: gerçek bir sunucuya karşı
  **63/63 geçti, 0 atlandı, 0 başarısız.**
- Testler çürümemiş. Bir haftalık değişikliğin ardından hiçbiri kırılmadı.
- `EO_TEST_PG_PORT` sayesinde 5432'si dolu bir geliştirme makinesi de paketi
  çalıştırabiliyor; "yerelde çalıştırılamaz" mazereti ortadan kalktı.
- Atlamanın sessizce geri gelmesi artık mümkün değil; son adım onu kırmızıya
  çevirir.
- Windows işi dokunulmadan duruyor, dolayısıyla DPAPI kapsamı kaybedilmedi.

### Olumsuz / kabul ettiğimiz bedel
- **İki iş, iki derleme.** Depolama projesi hem Windows hem Linux işinde
  derleniyor. CI süresi ve dakikaları artıyor; karşılığı ölçülen kapsamdır.
- Hazırlık döngüsü elle yazıldı. `services:`'in sağlık denetimi bedavaydı;
  60 saniyelik `pg_isready` döngüsü bakım gerektiren bir koddur.
- `postgres:18` etiketi sabitlenmedi, sürüm içinde kayar. Yama sürümleri
  arasında bir davranış değişirse CI önce kırılır, sonra anlaşılır.
- Windows işinin günlüğünde 52 "atlandı" satırı görünmeye devam ediyor.
  Yorumla açıklandı, ama okuyan biri için hâlâ bir gürültüdür.
- **Bu iş CI'da hiç çalıştırılmadı.** Çalışamazdı; bu değişikliğin kendisi CI'a
  girmeden önce doğrulanamaz. Aşağıdaki bölüm bunu ayrıca kaydeder.

### Bu ADR yazılırken doğrulananlar ve doğrulanamayanlar

`docs/live-verification.md` kanıtın iddiadan önce gelmesini şart koşar, bu
yüzden ayrımı açıkça yazıyorum.

Ölçülenler — geliştirme makinesinde, 5433 portunda kurulan tek kullanımlık bir
PostgreSQL 18.6 kümesine karşı:

- Parolasız koşu: 63 testin 11'i geçti, **52'si atlandı.** Görev tanımındaki
  sayı birebir doğrulandı.
- Parolalı koşu: **63 geçti, 0 atlandı, 0 başarısız**, 5 saniye. Arka arkaya
  dört koşuda aynı sonuç; kararsızlık gözlenmedi.
- Şema başına yalıtım gerçek: her test metodu kendi `LiveDatabase` örneğini
  kurar. Koşu sırasında örneklendi — **aynı anda 6 `eo_test_` şeması ve 11
  bağlantı** yan yana durdu, hepsi geçti. Koşu bittiğinde geriye kalan şema
  sayısı **0**; temizlik çalışıyor.
- `ci.yml` bir YAML ayrıştırıcısıyla çözümlendi; iş grafiği ve her adımın
  kabuğa gideceği hâli denetlendi.
- Atlamayı yakalayan son adımın betiği YAML'den **çıkarılıp** yukarıdaki iki
  koşunun gerçek `trx` dosyalarına karşı çalıştırıldı: geçen koşuda 0 ile,
  atlayan koşuda 1 ile çıktı ve doğru hata mesajını yazdı.
- `postgres:18` etiketinin yayında ve güncel olduğu doğrulandı.

Doğrulanamayanlar:

- **İşin GitHub Actions üzerinde çalıştığı.** Hiçbir koşu yapılmadı ve
  yapılamazdı. `docker run` adımları, `$GITHUB_ENV` aktarımı, `::add-mask::`
  ve `pg_isready` döngüsü yalnızca okunarak denetlendi.
- `actionlint` çalıştırılamadı. Görev tanımı `authoring-github-workflows`
  becerisi üzerinden erişilebilir olduğunu söylüyordu; bu beceri bu makinede
  **kurulu değil** ve sistemde `actionlint` ikilisi yok. YAML geçerliliği bir
  ayrıştırıcıyla gösterildi, ancak bu Actions şemasının denetlenmesi değildir.
- Testlerin Linux üzerinde davranışı. Yerel koşu Windows'ta yapıldı; satır sonu,
  yerel ayar veya zaman dilimine duyarlı bir davranış varsa Linux'ta ilk koşuda
  ortaya çıkacaktır.

### Bu kararı yeniden değerlendirmemiz gereken durum
- GitHub Actions Windows koşucularında `services:` desteklerse: iki iş tekrar
  teke inebilir ve elle yazılmış hazırlık döngüsü düşer.
- Depolama paketi yavaşlar ya da kapsayıcı başlatma kırılganlaşırsa:
  Testcontainers (Alternatif E) yaşam döngüsünü teste taşıyıp yerel ile CI'ı
  aynı yola sokar ve yeniden tartılmayı hak eder.
- Depoya Windows'a özgü bir depolama davranışı girerse: o test Linux işinde
  anlamını yitirir ve kapsamın nerede durduğu yeniden çizilmelidir.
- Bir PostgreSQL yama sürümü CI'ı kırarsa: etiketi `postgres:18.x` biçiminde
  sabitlemek ve yükseltmeyi bilinçli bir işe dönüştürmek gerekir.
