# ADR-0021: Depolama gecikmesi kör noktası datastore başına raporlanır; kaçış yolu toplu temizlemedir

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0007 (bilgi mimarisi ve alarm merkezileştirme),
  ADR-0013 (operatör eylemleri ve atıf), ADR-0018 (sağlık alarmlardan türer),
  README ilke 4 (gürültü operatörün düşmanıdır)

## Bağlam

`StorageLatencyBlindSpot` canlı ortamda çalıştı ve **25 kez** ateşledi:
41 datastore'un 25'inde Storage I/O Control etkin değil. Alarm kutusu
9 alarmdan 34'e çıktı.

Kuralın yazarı datastore başına alarmı bilerek seçmişti ve gerekçesi sağlamdı:
SIOC datastore başına etkinleştirilir, dolayısıyla düzeltme de datastore
başınadır. Tek bir ortam geneli alarm bir ilerleme çubuğuna dönüşür — operatör
sekiz birimde SIOC'u açar, alarm kıpırdamaz, çünkü otuz üç tane kalmıştır.
SIOC'un bilerek kapalı bırakıldığı tek birim için kabul edilemez, çünkü kabul
etmek diğer kırkını da susturur. Ve `EntityId` taşımadığı için aşağı akıştaki
hiçbir şey onu ilgili birime bağlayamaz.

25 alarmın kendisi sorun değil. Operatör SIOC'u açarsa hepsi çözülür; eyleme
geçilince temizlenen tek seferlik bir dalga dürüsttür.

Sorun diğer daldı. Operatör **"biz burada SIOC kullanmıyoruz"** derse — SIOC'un
gerçek maliyetleri olduğu için meşru bir karar — geriye kalıcı 25 uyarı kalır.
İddia şuydu: bir karar, yirmi beş eylem. Referans araştırması mekanizmayı
açıkça kaydetmişti: *"birinci günde yüzlerce bulgu, birinci haftada kapatılmış
bir alarm kutusu — 400 tanımlı bir ürün pratikte böyle 0 tanımlı bir ürün
hâline gelir."*

Değerlendirilecek öneri şuydu: **oran bulgunun şeklini değiştirsin.** Az sayıda
birim etkilenmişse "bu birimler atlanmış" → birim başına; birimlerin çoğu
etkilenmişse "bu ortam SIOC kullanmıyor" → tek bir politika kararı.

## Karar

**Kuralı olduğu gibi bırak.** Oran, alarmın kimliğini değil yalnızca metnini
belirlemeye devam eder; alarm datastore başına kalır.

"Bir karar, yirmi beş eylem" önermesi **yanlış çıktı.** Kaçış yolu zaten
mevcut ve doğru katmanda: `AlertOperations.ClearMany`, `POST /alerts/clear-many`
ve alarm kutusunun tümünü-seç kutucuğu. Operatörün temizlemesi yapışkandır —
aynı birimlerin sonraki döngüde yeniden gözlemlenmesi alarmı ne yeniden açar ne
de yeniden bildirir.

Kapsam: bu ADR kuralın eşiklerini, kategorisini veya ifadesini tartışmaz.
Yalnızca "kaç alarm" sorusunu ve oranın kimliğe girip giremeyeceğini kapatır.

## Gerekçe

Ölçüldü, varsayılmadı. Gerçek `AlertReconciler` üzerinde çalıştırılan bir sonda
üç şeyi gösterdi:

1. **Kaçış yolu bugün çalışıyor.** 25 alarm, tek bir toplu temizleme:
   görünür 0, bildirim 0, 25 yapışkan temizleme. Birimler raporlamaya devam
   ettiği hâlde kutu boş kalıyor.

2. **Şekil değişimi operatör kararlarını yok ediyor.** Oran eşiği geçilip
   alarm tek bir ortam geneli alarma dönüştüğünde, 25 birim başına parmak izi
   artık gözlemlenmez. `AlertLifecycle.OnAbsent` bunları **emekliye ayırır** —
   kodun kendi yorumuyla: *"yapışkan temizleme burada biter… bir daha dönerse
   yeniden doğar ve yeniden bildirebilir."* Ölçüm: 25 emekli, 0 hayatta kalan
   temizleme.

3. **Geri dönüşte operatör yeniden rahatsız ediliyor.** Eşik geri geçildiğinde
   25 alarm sıfırdan doğdu ve **25'i birden yeniden bildirildi.**

Uçurumu tetiklemek için operatörün hiçbir şey yapması gerekmiyor. Yük kapısı
saniyede bir işlem istiyor; SIOC'u kapalı bir birim tek bir sessiz 20 saniyelik
örnekte paydadan değil ama paydan düşer. Yani **tek bir birimin bir döngü
boyunca boşta kalması**, 25'inin tamamı için şekli çevirmeye yeter. Birim
başına geçici bir durum, ortam çapında bir alarm fırtınasına yükseltilir.

`HysteresisPolicy` bunu çözmez ve çözemez: alarmı sönümler, kipi değil. Bant
histerezisi (örneğin %70'te gir, %50'de çık) hangi kipte olunduğunun
hatırlanmasını gerektirir; kural ise saf bir fonksiyondur — durumu, saati ve
hafızası yoktur. Kipi hatırlatmak `MonitoringCycle`'ın işidir ve bu görevin
kapsamı dışındadır.

Histerez çözse bile ikinci bir kusur kalırdı: operatör **doğru yönde ilerleme
kaydettiğinde cezalandırılır.** Ortam geneli alarmı kabul edip sonra altı
birimde SIOC'u açan operatör, 25/41'den 19/41'e düşer, eşiği aşağı geçer ve
tek kabul ettiği kararın yerine **19 yeni alarm** alır. Bu bir salınım değil,
tek yönlü ve kasıtlı bir geçiştir; histerez tam olarak bunu sönümleyemez.

Mevcut şekilde ise ilerleme sorunsuz: parmak izi oranı taşımadığı için sayı
25'ten 19'a düşerken her alarmın metni değişir, kimlikleri değişmez, on dokuz
temizleme ayakta kalır ve düzeltilen altı birim sessizce emekli olur. Bu
davranış artık bir testle sabitlenmiştir.

## Değerlendirilen alternatifler

### Alternatif A: Oran şekli değiştirsin (eşikli kip geçişi)
Önerilen okuma buydu ve bulgunun oranla gerçekten değiştiği fikri doğrudur.
Reddedilme nedeni bulgu değil **kimlik**: kip değişimi parmak izi kümesini
değiştirir, bu da her geçişte operatörün kayıtlı kararını sessizce siler ve
geri dönüşte yeniden bildirir (ölçüldü: 25 emekli, 0 hayatta kalan, 25 yeniden
bildirim). Kip hafızası olmadan uçurum güvenli hâle getirilemez, kuralın ise
hafızası yoktur. Ortam sabit biçimde eşiğin bir tarafında dursaydı bu alternatif
daha iyi olurdu; 41'in 25'i tam ortada duruyor ve yük kapısı onu her döngüde
oynatıyor.

### Alternatif B: Kuralı bırak, kaçış yolunu düzelt
Doğru teşhis — ve **zaten yapılmış.** `ClearMany` uygulama katmanında,
`/alerts/clear-many` API'de, tümünü-seç ve toplu çubuk arayüzde mevcut;
`AlertOperations.AcknowledgeMany` üzerindeki yorum bu vakayı neredeyse kelimesi
kelimesine tarif ediyor: *"Bir arızalı anahtardan gelen yirmi alarm, operatörün
üstlendiği tek bir problemdir, yirmi karar değil."* Yapılacak yeni bir şey
kalmadığı için bu ADR onu inşa etmez, yalnızca kaydeder ve bir testle korur.

### Alternatif C: Her zaman tek alarm, birimleri listele
En basiti ve yazarın savunduğu birim başına `EntityId`'den vazgeçer. Ayrıca
yukarıdaki (2) ve (3) numaralı sonuçların kalıcı hâli: kutuda tek satır
olurdu ama hangi birim olduğu kaybolur, aşağı akış onu bir varlığa bağlayamaz
ve SIOC'un bilerek kapalı olduğu birim ayrı ayrı kabul edilemez.

### Alternatif D: Hiçbir şey yapma ve hiçbir şey kaydetme
Seçilen davranış bu, ancak kayıtsız bırakmak ADR README'sinin açıkça
engellemek istediği durumdur: makul alternatifleri olan ve sezgiye aykırı
görünen ("25 alarm neden azaltılmadı?") bir karar, altı ay sonra yeniden
açılacaktır.

## Sonuçlar

### Olumlu
- Operatörün "SIOC kullanmıyoruz" kararı tek bir toplu temizlemeye mal olur,
  kalıcıdır ve `ADR-0013` uyarınca bir isimle kaydedilir.
- SIOC'u açık olan 16 birim bulgunun dışında kalır; hiçbiri yanlışlıkla
  susturulmaz.
- Kısmi ilerleme cezalandırılmaz: sayı düşerken kimlikler sabit kalır.
- Ortama yeni eklenen, SIOC'u kapalı bir datastore yeni bir alarm üretir —
  doğrudur, yeni bir birim gerçekten yeni bir karardır.
- Kuralın kaynak kodu değişmedi; 22 mutasyonun arkasındaki davranış olduğu
  gibi duruyor.

### Olumsuz / kabul ettiğimiz bedel
- **İlk dalga gerçekten 25 alarmdır.** Operatör toplu temizlemeyi bilmiyorsa
  kutu gerçekten 34 satır gösterir. Bu bir keşfedilebilirlik bedelidir ve
  kuralın değil arayüzün üstündedir.
- Toplu temizleme hâlâ üç hareket ister: filtrele, tümünü seç, temizle. Tek
  tıklık bir "bu bulguyu bu ortamda kullanma" anahtarı değildir.
- Bulgunun oranla değiştiği gözlemi **doğru kalır** ve bu ADR onu bir cümleye
  indirger. 41/41'de politika cümlesi çıkar, 40/41'de çıkmaz; aradaki fark tek
  bir yapılandırılmış birimdir ve bu sertliği bilerek kabul ediyoruz.
- Yük kapısının kendi churn'ü duruyor: SIOC'u kapalı bir birim bir döngü boşta
  kalırsa temizlenmiş alarmı emekli olur ve döndüğünde yeniden bildirir. Bu
  şekil değişiminden bağımsız, önceden var olan ve **tek birimle sınırlı** bir
  davranıştır; bu görevde ölçüldü ama düzeltilmedi.

### Bu kararı yeniden değerlendirmemiz gereken durum
- Ürün bir bulguyu ortam ölçeğinde bir kez kapatmayı taşıyabilen bir
  yapılandırma kazanırsa (Alternatif B'nin gerçek, daha büyük hâli): o zaman
  birim başına alarm doğru **ve** sessiz olur ve bu ADR'nin bedel bölümü düşer.
- Toplu temizlemenin canlı ortamda fiilen kullanılmadığı görülürse: sorun
  keşfedilebilirliktedir ve arayüzde çözülmelidir, kuralda değil.
- Kurala bir kip hafızası verilebilir hâle gelirse (`MonitoringCycle` döngüler
  arası durum taşırsa): Alternatif A'nın uçurumu güvenli kılınabilir ve yeniden
  tartılmayı hak eder.
