# ADR-0009: Değerlendirme kapsamı ve köken damgası

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0004 (varlık yaşam döngüsü), ADR-0005 (collector sözleşmesi),
  ADR-0007 (alarm merkezileştirme)

## Bağlam

ADR-0005 toplamayı iki ayrı ritme ayırdı: envanter beş dakikada bir, metrik otuz
saniyede bir. `Host.AllInOne` bileşim kökünü yazarken bu iki döngü ilk kez aynı
durum deposunun üzerinde birlikte çalıştı ve ayrı ayrı doğru olan üç parça, bir
arada yanlış çıktı.

Üçü de **sessiz** hatalardı. Hiçbiri istisna atmıyor, log satırı üretmiyor veya
ekranda tuhaf görünmüyordu; ürün her üç durumda da çalışıyor gibi duruyordu.
Kayıt altına alınmalarının sebebi bu: aynı sınıftan bir dördüncüsü kolayca
yazılabilir.

### 1. Hızlı döngü, yavaş döngünün alarmlarını kapatıyordu

`AlertReconciler` kendisine verilen listeyi **tüm gerçek** kabul eder; depoda
olup gözlemde olmayan her alarm "artık yok" sayılır. Bu, tek bir değerlendirme
içinde doğrudur ve iki değerlendirme aynı depoyu paylaştığında felakettir:
envanter döngüsünün az önce açtığı donanım alarmı, otuz saniye sonra metrik
döngüsü tarafından kapatılıyordu. Beş dakika sonra envanter tekrar açıyordu.

Sahada görülecek belirti "alarm kaybolup geri geliyor" olurdu — yani bozuk bir
sistem gibi değil, **titreyen bir alarm** gibi. Üstelik ürünün flap dedektörü
bunu gerçek bir kararsızlık sanıp üstüne bir alarm daha üretirdi.

### 2. Tek vCenter, iki collector, tek sağlık kaydı

`CollectorHealth` yalnızca `InstanceId` ile anahtarlanıyordu. Aynı vCenter'ı iki
collector okuduğu için otuz saniyelik döngünün her başarısı, beş dakikalık
döngünün arka arkaya hata sayacını sıfırlıyordu. Sonuç: envanter okuması kalıcı
olarak bozuk olan bir kurulumda devre kesici hiç açılmıyor, "collector'a
ulaşılamıyor" alarmı hiç doğrulanmıyordu.

Bu ayrım uydurma değil, sahada gerçek: bir servis hesabı envanteri listeleyebilip
istatistik seviyesi yüzünden metrik alamayabilir. "Envanterinizi listeleyemiyoruz"
ile "metriklerinizi okuyamıyoruz" farklı problemlerdir ve farklı düzeltmeleri
vardır.

### 3. Hiçbir varlık kaybolmuyordu

ADR-0004 kaybolma kuralını `Entity.SourceInstanceId` üzerine kurdu: yalnızca
cevap vermiş bir kaynak kendi varlığını kaybolmuş yapabilir. vSphere envanter
collector'ı bu alanı hiç doldurmuyordu. Boş bir kaynak kimliği hiçbir zaman
"cevap verenler" kümesinde bulunmadığı için hiçbir varlık kaybolmuyor, hizmetten
çıkarılmış bir host grafta süresiz olarak son bilinen rengiyle kalıyordu.

## Karar

### 1. Bir değerlendirme yalnızca değerlendirdiğinin kaderini belirleyebilir

Bu, ADR-0004'ün `EntityGraph.Merge` için koyduğu kuralın aynısıdır; alarmlara
uygulanmış hâli. `AlertDefinition` ve `AlertInstance` bir **kapsam** (`Scope`)
taşır, depo kapsam başına dilimlenir, ve her döngü yalnızca kendi dilimini okur
ve yazar.

Kapsam **role göre** belirlenir (`inventory`, `observation`), kaynağa göre değil.
Kaynak başına kapsam, yapılandırmadan çıkarılan bir vCenter'ın alarmlarını
sonsuza dek uzlaştırılmamış bırakırdı — o kapsamı bir daha hiçbir döngü
değerlendirmeyeceği için. İki rol her zaman var olduğundan hiçbir alarm öksüz
kalmaz.

`AlertReconciler`'ın kendisi değişmedi ve saf kaldı. Dilimleme depo sınırında
yapılır; uzlaştırıcı hâlâ kendisine verilenin tüm gerçek olduğunu varsayar, ama
artık ona daha küçük ve tutarlı bir gerçek veriliyor.

### 2. Collector sağlığı kaynak **ve** rol ile anahtarlanır

`CollectorHealth.Role` eklendi. "Ulaşılamıyor" alarmının parmak izi de rolü
içerir, yoksa iki döngü birbirinin alarmını kapatmaya devam ederdi.

Alarm başlığı da rolü söyler: *Collector unreachable (inventory)*. Operatöre
söylenecek şey "vCenter'a ulaşılamıyor" değil, "vCenter'dan envanter
okuyamıyoruz ama metrikleri okuyabiliyoruz" — bu ikisi farklı aksiyonlardır.

### 3. Köken damgası collector'a bırakılmaz, pipeline vurur

`InventoryCollectionPipeline` her snapshot'ın içindekilere snapshot'ın kendi
kaynak kimliğini ve kapsamını damgalar. Collector yazarından bunu hatırlamasını
istemek, ileride yakalanmayı istemektir: iki alan da davranışı belirliyor ve iki
alan da eksik olduğunda görünmüyor.

Snapshot zaten bir kaynağa atfedilmiş durumda; bu yalnızca o atfı içindekilere
yayıyor.

### 4. Bildirim gönderildikten sonra işaretlenir

`AlertInstance.PendingNotification` sonraki her gözlemde korunur — gönderilemeyen
bir bildirimin unutulmaması için. Bunun sonucu, işaretlemeyi atlamanın alarmı
her döngüde yeniden göndermesidir.

`MonitoringCycle` bu yüzden dağıtıcıyı çağırır ve **döndükten sonra**
işaretler. Dağıtıcı hata verirse hiçbir şey işaretlenmez ve sonraki döngü tekrar
dener: en-az-bir-kez. Mükerrer bir bildirim can sıkıcıdır; düşen bir bildirim
ürünün var olma sebebinin başarısızlığıdır.

## Sonuçlar

### Olumlu

- Alarm gövdesi iki döngü arasında dengede kalır; envanter alarmları artık
  metrik döngüsünün hızına maruz değil.
- "Envanter okunamıyor" ile "metrik okunamıyor" ayrı ayrı görünür ve ayrı ayrı
  devre kesiciye tabidir.
- Kaybolma kuralı fiilen çalışır; ADR-0004 artık yalnızca yazılı değil.
- Yeni bir collector yazarı köken alanlarını bilmek zorunda değil.
- Üçüncü bir ritim (örneğin bir kural motoru) eklemek yeni bir kapsam sabiti
  eklemekten ibaret.

### Olumsuz / kabul ettiğimiz bedel

- Kapsam, alan modelinde taşınan ve ihmal edilirse sessizce yanlış davranan bir
  alan daha. Pipeline damgası bunu üretim yolunda kapatıyor ama alarm üreten
  ileride başka bir yol açılırsa aynı tuzak geri gelir.
- Depo artık kapsam başına dilim tutuyor; kalıcı depoya geçildiğinde bu
  dilimleme şemaya da yansımak zorunda.
- En-az-bir-kez bildirim, dağıtıcı yavaşsa mükerrer bildirim üretebilir. Kasıtlı;
  tersi kabul edilemez.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Kapsam sayısı ikiden fazlaya çıkıp roller arası alarm devri gerekirse
  (örneğin bir alarmın envanterde açılıp kural motorunda kapanması), kapsam
  devrini tanımlayan yeni bir ADR gerekir.
- Uzlaştırma kalıcı bir depoya taşındığında "verilen her şey tüm gerçektir"
  varsayımı sorgu sınırlarıyla birlikte yeniden değerlendirilmeli.
