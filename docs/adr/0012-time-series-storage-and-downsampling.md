# ADR-0012: Zaman serisi depolama ve aşağı örnekleme

- **Durum:** Kabul edildi — **saklama süreleri ADR-0017 ile güncellendi**
  (saatlik kademe 400 → 90 gün; üç kademeli tasarım, katlama sırası ve beş
  sayılı kova aynen geçerli)
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (dağıtım kısıtı), ADR-0005 (collector sözleşmesi),
  ADR-0007 (bilgi mimarisi), ADR-0011 (durum kalıcılığı)

## Bağlam

ADR-0011 ölçümleri kapsam dışı bırakmış ve gerekçesini yazmıştı: alarm ve graf
durumu küçük, işlemsel ve oku-değiştir-yaz; metrikler ekleme-ağırlıklı,
yüksek hacimli ve aralık sorgulanan bir yük. İkisini tek kararla çözmek ikisini
de kötü çözer.

Sonucu şuydu: gözlemler toplanıyor ama hiçbir yere yazılmıyordu. ADR-0007'nin
**Analiz** grubu ("nereye gidiyoruz") boştu, ve ürün her döngüde ölçtüğü şeyi
bir sonraki döngüde unutuyordu.

Hacim kararı belirliyor. Orta büyüklükte bir ortam — 30 host, 800 VM, 40
datastore — metrik sözleşmesindeki sayaçlarla örnekleme aralığı başına yaklaşık
sekiz bin örnek üretir. Otuz saniyede bir, günde yaklaşık yirmi beş milyon satır.
Tam çözünürlükte sonsuza kadar saklamak bir seçenek değil ve hiç olmadı.

Her izleme ürünü bunu aynı şekilde cevaplıyor: yeniyi ince, eskiyi kaba tut.
Gerçek kararlar ne kadar kaba, ne kadar uzun, ve yolda neyin kaybedildiği.

## Karar

### 1. Ayrı dosya, aynı motor

Ölçümler `metrics.db` dosyasında; durum `observatory.db`'de kalıyor. Aynı SQLite,
farklı dosya.

İkisi tamamen farklı büyüyor ve çürüyor. Ölçüm dosyası ekleme-ağırlıklı ve
birkaç dakikada bir retention tarafından satır siliniyor — bu zamanla dosyayı
parçalar. Alarm durumunu o dosyadan uzak tutmak iki şey sağlıyor: büyümüş veya
bozulmuş bir metrik geçmişi alarm üretimini yanında götüremiyor, ve destek için
alınan bir durum kopyası gigabayt değil kilobayt oluyor.

### 2. Kova başına tek sayı değil, beş sayı

Her kova `min`, `max`, `sum`, `count` ve `last` tutuyor.

Yalnızca ortalamayı saklamak, bir izleme ürününün var olma sebebini kaybetmesinin
klasik yolu: bir saat içinde iki dakika %100'e yapışmış bir host ortalamada
yaklaşık %3 eder ve tamamen kaybolur. Bu ADR yazılırken canlı doğrulandı — beş
dakikalık kovada ortalama 47.7, **max 97.6**; saatlik kovaya inildiğinde ortalama
~20'ye düşüyor ve tepe yalnızca `max` sayesinde hayatta kalıyor.

`sum` ve `count`, ortalamanın kendisi yerine saklanıyor çünkü **ortalama yeniden
toplanamaz**: kaç örnekten geldiğini bilmeden iki ortalamayı birleştiremezsiniz.
Beşinin de yeniden toplanması tam: minimumların minimumu, maksimumların
maksimumu, toplamların toplamı, sayıların sayısı, ve en son kovanın son değeri.

`last` ise `Latest` tipi sayaçlar için: bir datastore'un boş alanının bir saatlik
ortalaması kimsenin sormadığı bir büyüklük, güncel rakam ise sorduğu.

### 3. Üç kademe, ve aralarındaki kural

| Çözünürlük | Saklama | Cevapladığı soru |
|---|---|---|
| Ham (örneklendiği gibi) | 2 gün | "Saat 03:14'te tam olarak ne oldu" |
| 5 dakika | 30 gün | "Bu, yamadan sonra mı başladı" |
| 1 saat | 400 gün | Kapasite planlama, yıla yıl karşılaştırma |

Depolama maliyeti her adımda yaklaşık on kat düşüyor, yani uzun kuyruk neredeyse
bedava; diskin neredeyse tamamı ham penceresi.

Saatlik kova, **beş dakikalık kovalardan** katlanıyor, ham örneklerden değil —
o noktada ham örnekler çoktan silinmiş oluyor. Bu ancak yeniden toplama tam
olduğu için sağlam, ki beş sayının seçilme sebebi tam olarak bu.

### 4. Katlamak silmekten önce gelir

Tek geçiş, sabit sıra. Ters sırada bir örnek özetlenmeden silinir ve kayıp
sessiz ve kalıcıdır; tek belirti olması gerekenden boş bir grafik.

Hâlâ dolmakta olan bir kova katlanmıyor. Erken özetlenirse yarım kovanın özeti
olur ve hiçbir şey ona geri dönmez.

Geçiş **idempotent**: bir kova kaynaklarından hesaplanıyor, üzerine biriktirilmiyor.
Yarıda kesilen bir geçiş tekrar çalıştırılabilir. Ayrıca bir su işareti ile
sınırlı, yani bir geçişin maliyeti servis bir saattir mi bir yıldır mı çalışıyor
fark etmiyor.

### 5. Çözünürlüğü aralık seçer, istemci değil

Otuz günlük ham veri isteyen bir istemci, çizemeyeceği ve artık elimizde olmayan
seksen bin nokta istiyor demektir. Sunucu aralığa ve nokta bütçesine bakıp en
ince uygun çözünürlüğü seçiyor.

Kullanılan çözünürlük **geri bildiriliyor ve arayüz onu gösteriyor.** Saatlik
ortalama çizdiğini söylemeyen bir grafik canlı ölçüm gibi okunur; ilke 1'in
sessiz ihlali tam olarak budur.

### 6. Boşluk boşluktur

Eksik kova eksik olarak dönüyor, sıfırla doldurulmuyor. Sıfır "baktık ve bir şey
yoktu" demek; boşluk "bakmıyorduk" demek. İkincisi doğruyken birincisini çizmek,
bir izleme ürününün söylediği ilk yalandır.

Aynı ayrım "bu sayaç bu varlık için hiç kaydedilmedi" ile "istenen pencerede
kayıt yok" arasında da var ve ikisi ayrı mesajlarla gösteriliyor: ilki platform
bunu vermiyor demek, ikincisi genelde bir collector sorunu.

### 7. Ham değer saklanır, dönüştürülmüş değil

Bir sayının ne anlama geldiği birimi ve rollup tipiyle taşınıyor; dönüşüm yazma
anında uygulanmıyor. Aksi halde saklanan geçmiş, onu yazan kodun sürümüne
bağımlı olurdu ve bir dönüşüm düzeltmesi zaten yazılmış veriye asla
uygulanamazdı.

## Sonuçlar

### Olumlu

- Analiz grubu açıldı: ürün artık ölçtüğünü hatırlıyor.
- Tepe noktalar aşağı örneklemeden sağ çıkıyor, ki bir izleme ürününde asıl
  bakılan şey onlar.
- Bir yıldan uzun geçmiş, diskte ham pencerenin yanında ihmal edilebilir yer
  kaplıyor.
- Kurulum yüzeyi hâlâ değişmedi: iki dosya, servis yok, appliance yok.
- Metrik dosyası ayrı yapılandırılabiliyor; sistem diski küçük olan kurulumlar
  onu başka yere koyabilir.

### Olumsuz / kabul ettiğimiz bedel

- **Yüzdelikler türetilemiyor.** p95 gecikme, saklanan beş sayıdan
  hesaplanamaz. Ham pencere (2 gün) olay incelemesinin p95'e ihtiyaç duyduğu
  aralığı kapsıyor; uzun vadeli p95 bir eskiz (t-digest, DDSketch) gerektirir ve
  kendi kararıdır. Bir depolama gecikmesi ürününde bunun önemli olduğunu
  biliyoruz — bu yüzden bilinçli bir erteleme olarak yazıyoruz, atlanmış bir
  ayrıntı olarak değil.
- **Okunabilirlik boyuta yenildi.** Durum şeması isimleri saklıyor; burada seri
  sözlüğü ve tamsayı zaman damgaları var. Kasıtlı bir istisna, alışkanlık değil:
  durum veritabanı isimleri karşılayabilecek kadar küçük, bu değil.
- **SQLite tek yazıcı.** Bugünkü yük (aralık başına bir büyük transaction) buna
  fazlasıyla uyuyor, ama bir tavan.
- **Retention silmeleri dosyayı parçalar.** `VACUUM` henüz hiç çalıştırılmıyor;
  dosyanın gerçekte nasıl büyüdüğünü ölçmeden bir bakım penceresi eklemek tahminle
  optimize etmek olurdu.
- Ham çözünürlük genişliği (30 sn) nominal. Gerçek örnekleme aralığı gerçek
  zamanlı beslemede 20 saniye, tarihsel beslemede 300 saniye; bu rakam yalnızca
  bir aralığın kaç nokta üreteceğini tahmin etmek için kullanılıyor.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Yüzdelik gerektiğinde: eskiz tabanlı bir özet yeni bir ADR ile eklenir.
- Toplama ile web ayrı proseslere bölündüğünde: iki yazıcı SQLite'ın modelini
  aşar.
- Ölçülmüş bir kurulum dosya boyutunu veya sorgu süresini sorun haline
  getirdiğinde — o noktada veri var demektir ve karar tahmine dayanmaz.
