# ADR-0020: Anahtar halkasının kaybolabilirliği açılışta denetlenir, ama servis durdurulmaz

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0010 (kimlik bilgileri yapılandırma dosyasında bulunmaz),
  ADR-0015 (bağlantılar üründen girilir), ADR-0016 (dış altyapı bağımlılıkları),
  README ilke 1, ilke 4

## Bağlam

ADR-0015 gereği bir vCenter parolası ürüne operatör tarafından yazılır ve
veritabanında şifrelenmiş olarak durur. Şifreyi çözen tek şey Data Protection
anahtar halkasıdır. Halka veritabanının içinde değildir ve olmaması da
kasıtlıdır: ikisini birden alan bir yedek, parolaları kullanılabilir hâlde alan
bir yedektir.

Bu doğru kural, bugün yanlış bir sonuca yol açtı. Halka şuraya
yapılandırılmıştı:

    C:/Users/<kullanıcı>/AppData/Local/Temp/eo-live/keys

`%TEMP%`. Windows Disk Temizleme, Depolama Algılayıcısı, her bakım betiği ve
disk dolduğunda kendiliğinden çalışan temizlik bu dizini siler. Silindi.
Klasör öğleden sonra yeniden oluşmuştu ve içinde tek bir anahtar vardı;
yapılandırılmış bir vCenter bağlantısının parolası artık çözülemiyordu.

Ürün bunu **söyledi** — parolası çözülemeyen bir bağlantı için "Collector
unreachable" üreten düzeltme bugün girdi. Doğru davrandı ve **geç** davrandı.
O satır göründüğünde kimlik bilgisi zaten kurtarılamaz durumdaydı: veritabanı
yedeğinde yok, çünkü veritabanı anahtarı hiç tutmadı.

"Halka ile veritabanı aynı yedeği paylaşmaz" kuralı, "halkayı atılabilir bir
yere koy" anlamına gelmiyor. İkisi de kalıcı olmalı ve ikisi de **ayrı ayrı**
yedeklenmeli. Yol artık kodun kendi varsayılanı olan
`C:/ProgramData/EnterpriseObservatory/keys`'e taşındı, ama bir sonraki kurulumu
aynı şeyi yapmaktan alıkoyan hiçbir şey yok.

Sorulacak soru şu: ürün kaybolabilir bir konum gördüğünde **açılmayı reddetmeli
mi**, yoksa açılıp yüksek sesle söylemeli mi?

## Karar

Anahtar halkasının nerede duracağı, hiçbir şey toplanmadan önce, Data
Protection kurulmadan denetlenir. Sonuç üç değerden biridir ve yalnızca biri
servisi durdurur.

| Bulgu | Davranış |
|---|---|
| Kalıcı dizin (ProgramData, operatörün seçtiği bir yol) | **Sessiz.** Hiçbir satır yazılmaz. |
| Kaybolabilir dizin (temp/tmp/temporary) | **Açılır**, `Critical` seviyesinde bir kez söyler. |
| Oluşturulamayan dizin | **Reddeder.** Servis durur. |
| Boş halka + üründen girilmiş bağlantı | **Açılır**, ayrı bir `Critical` satırı yazar. |

Kaybolabilir sayılan dizinler: yol bileşenlerinden biri `temp`, `tmp` veya
`temporary` olan her yol — bileşen olarak, alt dize olarak değil — artı bu
sürecin `%TEMP%` ve `%TMP%` değişkenlerinin gösterdiği kökler.

Kapsam dışı: İndirilenler, Geri Dönüşüm Kutusu, ağ paylaşımları. Bunlar da
kaybedilebilir ama varsayılan olarak kendiliğinden silinmezler; listeye
eklemek, ilke 4'ün yasakladığı yanlış pozitifi üretir.

## Gerekçe

Denetim öne alındı çünkü **halka kaybolduktan sonra söylenen her şey geç**.
Açılış anında ise durum tamamen kurtarılabilir: anahtarlar yerinde, parolalar
çözülüyor, taşıma bir ayar ve bir yeniden başlatma.

Durdurmama kararı, bu başlangıç yolundaki en yakın emsale — servisi durduran
`CredentialSourceGuard`'a — bilerek uymuyor. İkisi arasındaki fark, **düzeltmenin
nerede yapıldığı**:

- Bir ayar dosyasındaki parolayı çalışan ürün iyileştiremez. Düzeltme ürünün
  dışında, servis durmuşken yapılır: user secrets, ortam değişkeni. Orada
  durmak hiçbir şeye mal olmaz.
- Silinmiş bir halkanın düzeltmesi ise **ürünün içindedir**: bir yönetici oturum
  açar ve Connections ekranından parolaları yeniden girer. Açılmayan bir ürün
  bunu imkânsız kılar. Reddetmek, uyarıyı uyardığı kesintiye çevirir.

Buna izleme ürünü olmanın bedeli ekleniyor: açılmayan bir ürün hiçbir şeyi
izlemez. Sabah üçte servisi yeniden başlatıp ölü bulan operatör, gelen kutusunda
gürültülü bir uyarı bulan operatörden daha kötü durumdadır — ve estate'in geri
kalanı bu sırada izlenmeye devam eder. İlke 1 "bakmıyoruz" durumunun görünür
olmasını ister; görünürlük ürünün içindedir, ürünün yokluğunda değil.

Tek istisna, oluşturulamayan dizin. Orada durmak sertlik değil, ilke: yazılamayan
bir halka "bozulmuş ürün" değil, **kimlik bilgisi saklayamayan ürün**'dür.
Açılsaydı, formda parola kabul edip bir sonraki yeniden başlatmada kaybeden bir
servis üretirdi — açılmamaktan kötüdür, çünkü çalışmış gibi görünür. Veritabanı
birkaç satır yukarıda tam olarak bu gerekçeyle reddediyor.

### Silinmiş halkayı yeni kurulumdan ayırmak

Ayrılabiliyor, ve ayrım ADR-0015'in kendisinden geliyor: üründen girilmiş bir
bağlantı ancak halka onun parolasını şifrelediği için var olabilir. Dolayısıyla
**boş bir halkanın yanındaki üründen girilmiş bağlantı**, ikisi birden orijinal
olamayacak iki olgudur.

Yepyeni bir kurulumun henüz saklanmış bağlantısı yoktur — olamaz, kimse oturum
açmamıştır — ve bu yoldan sessizce geçer. Ayrım tam olarak budur.

Ayıramadığı şey: silinmiş bir dizin ile yolu yeni bir konuma değiştirilmiş bir
halka. Bu bir eksiklik değil — sonuç aynıdır (o parolaların şifrelendiği
anahtarlar burada değildir) ve çare de aynıdır.

## Değerlendirilen alternatifler

### Alternatif A: Kaybolabilir konumda açılmayı reddetmek

`CredentialSourceGuard` ile simetri kazandırırdı ve "bir dahaki kuruluma engel
ol" isteğini en kesin biçimde karşılardı. Seçilmedi çünkü reddetme, hiçbir şeyin
henüz kaybolmadığı bir durumda kesinti üretir. Anahtarlar okunabilir, parolalar
çalışıyor; tek yapılacak bir ayarı değiştirmek. Bunun bedeli olarak tüm estate'i
karanlığa gömmek, ilke 5'in "izleme aracı üretimi bozmaz" dediği şeyin tam
tersidir — ve düzeltmeyi yapacak kişi zaten o ayara ulaşabiliyorken servisi
durdurmak ona hiçbir şey kazandırmaz.

### Alternatif B: Silinmiş halka durumunda reddetmek

Daha da cazip: kimlik bilgileri zaten kayıp, ürün zaten kör. Seçilmedi çünkü bu,
tek çarenin ürünün içinde olduğu durumdur. Yönetici Connections ekranını açıp
parolaları yeniden girmeli; servis açılmazsa girecek bir yer yoktur. Reddetmek,
kurtarma yolunu kapatmak demektir.

### Alternatif C: `Warning` seviyesinde söylemek

Gürültü daha az olurdu. Seçilmedi çünkü bu satırın söylediği şey "her saklanmış
vCenter parolasının tek kopyası siliniyor olabilir". `Warning`, bu üründe bir
vCenter'ın yanıt vermemesiyle aynı seviyedir ve o günlerce sürer. Seviye
sonuçla orantılıdır; buradaki sonucun geri dönüşü yoktur.

### Alternatif D: Ürün içinde `Critical` alarm üretmek

İlke 1 "bakmıyoruz" durumunun **üründe** görünmesini ister, bu yüzden doğal
seçenek buydu. Şimdilik yapılmadı: alarm üretimi analiz katmanının işidir ve
bir açılış bulgusunu oraya bağlamak, bu kararın kendisinden daha büyük bir
değişikliktir. Günlük satırı bugünün cevabı, alarm yarınki — bu ADR yeniden
açılması gereken durumlar arasında sayıyor.

## Sonuçlar

### Olumlu

- Bugünkü olay, olmadan önce söylenir. Söylendiği anda hâlâ düzeltilebilirdir.
- Doğru kurulum sessizdir. ProgramData, operatörün seçtiği kalıcı bir yol ve
  henüz oluşturulmamış kalıcı bir dizin hiçbir satır üretmez — ilke 4.
- Kayıp kimlik bilgisi durumu, konum uyarısından ayrı bir olay kimliğiyle
  (1006, 1005 değil) raporlanır. Bir günlük hattı birine sayfa çağrısı çıkarıp
  diğerine çıkarmayabilir.
- Hiçbir mesaj anahtar malzemesi, parola veya dosya içeriği taşımaz. Yalnızca
  yollar ve sayılar.

### Olumsuz / kabul ettiğimiz bedel

- Kaybolabilir bir konumda ürün **çalışmaya devam eder**. Operatör satırı
  görmezden gelirse felaket yine de olur; bu kararla onu engellemiyoruz, yalnızca
  önceden ve düzeltilebilirken söylüyoruz.
- Kaybolabilirlik sezgisel bir testtir. Adında `temp` geçmeyen ve hiçbir ortam
  değişkeninin göstermediği bir betiğin her gece sildiği dizini göremeyiz.
- Silinmiş halka tespiti, **saklanmış bağlantı sayısına** dayanır. Hiç bağlantı
  girilmemiş, yalnızca yapılandırmadan beslenen bir kurulumda halka silinse bile
  bu satır çıkmaz — orada kaybedilecek bir şifreli parola da yoktur, ama
  anahtarların gittiği gerçeği yine de söylenmemiş olur.
- Açılış artık `ISourceConnectionStore`'u okur. Ucuzdur (mağaza zaten kurulumda
  belleğe yüklenir) ama açılış yolu ile kalıcılık arasında bir bağ daha demektir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Analiz katmanı açılış bulgularını alabilir hâle geldiğinde: Alternatif D'yi
  uygulayıp bu iki satırı üründe görünür birer `Critical` alarma bağlamalıyız.
  Günlük satırı, ilke 1'in istediği görünürlüğün tamamı değildir.
- Kaybolabilirlik testi bir kez bile yanlış pozitif ürettiğinde. Bu satırın tek
  sermayesi, göründüğünde inanılmasıdır.
- Bir kurulumda halka `temp` adı geçmeyen bir dizinde silindiğinde: sezgisel
  testin listesi yetmiyor demektir ve kural yeniden yazılmalıdır.
