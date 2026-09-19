# ADR-0013: Operatör eylemleri ve atıf

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0006 (web stack, kimlik doğrulama mekanizması),
  ADR-0007 (alarm merkezileştirme), ADR-0009 (değerlendirme kapsamı),
  ADR-0011 (durum kalıcılığı)

## Bağlam

Alarm yaşam döngüsü ilk günden beri operatör geçişlerini içeriyordu —
onaylama, kapatma, susturma — ve hepsi test edilmişti. Hiçbirine ulaşılamıyordu.
Ürün gösteriyor, operatör hiçbir şey yapamıyordu.

Eksik olan üç şey vardı ve ikisi teknik değildi.

**1. Kim yaptı?** ADR-0007 denetim izini "bu neden yandı ve kim kapattı"
sorusunun cevabı olarak tanımlıyor. Kimlik doğrulama henüz yok: ADR-0006
mekanizmayı seçti (aynı köken, çerez) ama kullanıcı deposunu değil.

**2. Kim yapabilir?** Kimlik doğrulama olmadan, porta erişebilen herkes alarm
kapatabilir. Bu bir izleme ürününde gerçek bir risk ve kazayla verilecek bir
karar değil.

**3. Döngü ne zaman araya girer?** Depo alarm durumunu kapsam başına toptan
yazıyordu: oku, uzlaştır, yaz. Bir operatör tam o aralıkta onaylarsa,
uzlaştırmanın sonucu onun değişikliğini sessizce eziyordu. Buton çalışmış gibi
görünür, alarm sonra yeniden açılır ve geriye hiçbir iz kalmaz.

## Karar

### 1. Doğrulanamayan aktör, doğrulanamadığı yazılarak kaydedilir

Doğrulayamadığımız bir ismi doğrulayabilmişiz gibi kaydetmek, isimsiz
kaydetmekten kötüdür: yetkili görünür ve değildir.

`OperatorIdentity` ismi ve **doğrulanıp doğrulanmadığını** birlikte taşıyor.
Denetim kaydına giren değer doğrulanmamışsa `unverified:` ile önekleniyor —
yanında ayrı bir bayrak olarak değil, çünkü ayrı bir alanı, bakması gerektiğini
bilmeyen bir okuyucu düşürebilir. Beş yıl sonra geçmişi okuyan birinin, o
tarihte kurulumun nasıl yapılandırıldığını bilmesi gerekmiyor.

### 2. Kimliksiz yazma varsayılan olarak kapalı, açıkça açılır

`Operations:AllowUnauthenticatedWrites` varsayılan `false`. Kapalıyken yazma
uçları 403 dönüyor ve **neden** döndüğünü, nasıl açılacağını söylüyor — mevcut
bir butona verilen çıplak bir 403 operatörü log'lara gönderir, orada da bir şey
yoktur.

Açıkken açılış anında uyarı basılıyor. Sertifika doğrulamasını gevşetmekle aynı
şekil, aynı gerekçe: yönetim ağında küçük bir ekip için makul bir takas
olabilir, yönlendirilmiş bir ağda değil — ve bu, ürünün operatör adına sessizce
vereceği bir karar değil.

Bu, kimlik doğrulamanın yerine geçmiyor. Onun eksikliğini **görünür** kılıyor.

### 3. Okuma, karar ve yazma arasında alarm durumu bırakılmaz

`IAlertStateStore` artık uzlaştırmayı bir geri çağırımla yapıyor: depo kendi
dilimini veriyor, saf uzlaştırıcı kararını üretiyor, depo sonucu yazıyor — ve
arada tutuşunu bırakmıyor.

Operatör değişiklikleri de aynı kilit altında (`Mutate`). Böylece bir döngü ile
bir operatör eylemi **sıralanıyor**: hangisi ikinci gelirse kazanıyor ve hiçbiri
kaybolmuyor. Ayrı bir oku/yaz ile pencere kısa ve kayıp görünmezdi, ki bu tam
olarak tasarımla ortadan kaldırılması gereken hata sınıfı.

Geri çağırım saf ve hızlı olmak zorunda; `AlertReconciler` ikisi de.

### 4. Değişiklik, çağıranın gördüğü kopyaya değil, depodaki kayda uygulanır

`Mutate` geçişi depodaki instance üzerinde çalıştırıyor. Son gördüğü hâl
üzerinden hareket eden bir istemci, o andan beri olan her şeyi geri alırdı —
başka bir operatörün onayı dahil.

### 5. Komutlar, kaynak değil

Uçlar `POST /api/alerts/acknowledge|clear|silence` ve parmak izi **gövdede**.
Parmak izi ayraç ve boşluk içeriyor; opak bir tanımlayıcıyı URL segmentine
koymak, üründeki her varlık sayfasının yanlış şeyi döndürmesine yol açan
hatanın ta kendisiydi (bkz. `EntityId.Separator`). Bunlar ayrıca bir operatörün
*yaptığı şeyler*, düzenlediği alanlar değil.

### 6. Susturmanın süresiz hâli yok

`Silence` bir son tarih istiyor, geçmişte olanı reddediyor. Süresiz kapatılan
bir şey, kimsenin geri açmayı hatırlamadığı bir şeydir; bunlarla dolu bir izleme
sistemi sessizce izlemeyi bırakmıştır. Ürün ilkesi 4.

### 7. Gitmiş bir alarma yapılan eylem hata değildir

Otuz saniye eski bir ekrandan hareket eden operatör yanlış bir şey yapmıyor.
`NotFound` 404 ile, arayüzün tazelemesi gereken bir sonuç olarak dönüyor;
istisna değil.

## Sonuçlar

### Olumlu

- Operatörün eline ilk kez kontrol geçti: onayla, sustur, kapat.
- Her değişiklik kalıcı denetim izine giriyor — kim, ne zaman, hangi geçiş.
- Onay bir sonraki toplama döngüsünü atlatıyor; canlı doğrulandı.
- Bir döngü ile bir operatör eylemi asla iç içe geçmiyor.
- Kimlik doğrulamanın yokluğu gizlenmiyor; kapalı bir kapı ve bir uyarı olarak
  görünür.
- Aynı eylemler gelen kutusunda ve varlık sayfasında aynı instance'lar üzerinde
  — ADR-0007'nin tek model kuralı, artık yazma tarafında da.

### Olumsuz / kabul ettiğimiz bedel

- **Kimlik doğrulama hâlâ yok.** Bu ADR onu ertelemenin bedelini görünür
  kılıyor, ödemiyor. `AllowUnauthenticatedWrites` açık bir kurulumda denetim izi
  "bu IP'den biri" demekten öteye gidemez.
- **Kilit, uzlaştırma boyunca tutuluyor.** Uzlaştırıcı saf ve mikrosaniyeler
  sürüyor, ama çok büyük bir kapsamda bu süre büyür ve o sırada bir operatör
  eylemi bekler. Ölçülmesi gereken bir tavan.
- **Rol yok.** Herkes her şeyi yapabiliyor; "kapatabilen" ile "görebilen"
  ayrımı kimlik doğrulama ile birlikte gelecek.
- **Toplu eylem yok.** Yirmi alarmı tek tek onaylamak sahada can sıkıcı olacak;
  olay gruplaması (ADR-0007 §5.1) ile birlikte ele alınmalı.
- Bakım pencereleri hâlâ bağlı değil: alan modelinde var, uzlaştırıcıya
  geçirilmiyor. Ayrı bir iş.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Kimlik doğrulama eklendiğinde: `AllowUnauthenticatedWrites` anlamını yitirir
  ve kaldırılması değerlendirilir; roller kendi ADR'sini gerektirir.
- Toplu eylem gerektiğinde: tek tek `Mutate` yerine kapsam düzeyinde bir işlem
  gerekebilir.
- Toplama ile web ayrı proseslere bölündüğünde: tek kilit iki prosese
  yetmez ve sıralama garantisi paylaşılan depoya taşınmak zorunda.
