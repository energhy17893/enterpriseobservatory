# ADR-0014: Yerel hesaplar ve çerez oturumları

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (dağıtım kısıtı), ADR-0006 (web stack, çerez tercihi),
  ADR-0011 (durum kalıcılığı), ADR-0013 (operatör eylemleri ve atıf)

## Bağlam

ADR-0013 operatöre kontrolü verdi ve kimlik doğrulamanın yokluğunun bedelini
görünür kıldı: `Operations:AllowUnauthenticatedWrites`. Bir koltuk değneğiydi ve
öyle olduğu yazılıydı. Denetim izi "bu IP'den biri" demekten öteye gidemiyordu,
ve rol diye bir şey yoktu — herkes her şeyi yapabiliyordu.

Asıl soru hesapların nereden geleceği.

## Karar

### 1. Temel yerel hesaplar, dizin entegrasyonu üstüne

Bir VMware ortamında ilk akla gelen Windows/AD entegrasyonu: operatörlerin zaten
hesabı var, yeni parola yönetilmiyor, grup üyeliği rolü bedavaya veriyor. Buna
rağmen **temel** o değil.

Sebep bu ürüne özgü: dizin çöktüğünde giriş yapılamayan bir izleme sistemi,
tam da birinin neyin bozuk olduğunu öğrenmesi gereken anda kullanılamaz. Ve
"dizin çöktü", bu ürünün söylemesi gereken şeylerden biri.

Dolayısıyla yerel hesaplar taban. Dizin entegrasyonu bunun **üstüne** gelir,
yerine değil — ve geldiğinde yerel hesaplar kırılma anı için kalır.

### 2. Parola: PBKDF2-HMAC-SHA256, 600.000 tur

Argon2id GPU'ya karşı daha iyi direnir ve bağımlılık bedava olsaydı seçim o
olurdu. ADR-0001 ürünü kurulacak hiçbir şeyi olmayan tek bir MSI'da tutuyor ve
doğru parametrelenmiş PBKDF2, ASP.NET Core Identity'nin kendi gönderdiği şey.

Parametreler saklanan değerin içinde: `pbkdf2-sha256$turlar$tuz$hash`. Kendini
tarif ediyor, yani veritabanına yıllar sonra bakan biri bunun ne olduğunu bu
dosyayı bulmadan anlayabiliyor — ve tur sayısını yükseltmek, hesapları
geçersiz kılmak yerine insanlar giriş yaptıkça yükseltmeyi mümkün kılıyor.

Her hesap kendi tuzunu alıyor: ortak tuz, kırılan bir parolanın onu seçmiş her
hesabı ele vermesi ve gökkuşağı tablosunun yapmaya değmesi demek.

### 3. Parola kuralı yalnızca uzunluk

En az 12 karakter, başka kural yok. Bileşim kuralları — bir rakam, bir sembol,
bir büyük harf — insanları ölçülebilir şekilde `Password1!`'e itiyor ve hem NIST
hem NCSC bunlara karşı tavsiyede bulunuyor. Saldırgana gerçekten maliyet çıkaran
şey uzunluk.

### 4. İlk hesap: tek kullanımlık kurulum jetonu

İki kötü alternatif var. **Varsayılan yönetici parolası** ürünlerin ele
geçirilme yolu ve internetteki her tarayıcı yaygın olanları biliyor. **İlk
açılışı açık bırakmak** ise kuruluma ilk ulaşanın sahibi olması demek.

Bu yüzden servis açılışta bir jeton üretip yalnızca kendi çıktısını zaten
okuyabilen birinin göreceği yere yazıyor. Jeton **saklanmıyor**: veritabanından
çalınacak bir şey yok ve servisi yeniden başlatmak yenisini üretiyor. Yalnızca
hiç hesap yokken işe yarıyor; bir yönetici oluştuğu anda kapı kalıcı olarak
kapanıyor, eski log dosyalarında kaç jeton dolaşırsa dolaşsın.

### 5. Her şey kimlik doğrulaması istiyor, okumalar dahil

Bu üründe okuma zararsız değil: ortamın host adları, adresleri ve seri
numaraları, ona saldırmadan önce isteneceklerin ta kendisi. Kapı butonların
etrafında değil, uygulamanın önünde.

Tek istisna `/api/auth/*`: durum, giriş, ilk kurulum.

### 6. Çerez, tarayıcı deposunda jeton değil

ADR-0006 bunu zaten seçmişti ve arayüz ile API'nin aynı kökenden sunulmasının
sebebi buydu. `HttpOnly` sayfaya sızan bir betiğin okumasını, `SameSite=Strict`
başka bir sitenin tarayıcıya gönderttirmesini engelliyor. `Secure`, isteğin
kendisi HTTPS ise — sabit `Always`, kapalı bir yönetim ağında düz http üzerinde
ürünü sebebini söylemeden kullanılamaz yapardı.

API asla giriş sayfasına yönlendirmiyor, durum kodu dönüyor. HTML'e giden bir
302, "oturumun doldu"yu sebepten çok uzakta bir JSON ayrıştırma hatasına
çeviren şey.

### 7. Üç rol

`Viewer` < `Operator` < `Administrator`, ve her biri altındakini içeriyor.
İnsanların gerçekten ihtiyaç duyduğunun ötesindeki her rol, atarken yanlış
yapılacak bir şey daha; izin modelini kimsenin anlamadığı bir ürün herkesin
yönetici olduğu bir üründe biter.

Politikalar adlandırılmış, çağrı yerinde rol dizesi yok — böylece yeni bir komut
eklemek, sessizce herkesin çalıştırabileceği bir komut ekleyemiyor.

### 8. Kilitlenme süreli, kalıcı değil

Beş hata, beş dakika. Kalıcı kilitlenme, giriş sayfasına ulaşabilen herkese
kasten kötü tahmin ederek her yöneticiyi kilitleme imkânı verir — kaba kuvvet
savunmasını hizmet engellemeye çevirir.

Kilitlenme hesapla birlikte saklanıyor: yalnızca bellekte tutulsaydı servisi
yeniden başlatmak onu aşmanın yolu olurdu.

### 9. Bilinmeyen kullanıcı adı, bilinen kadar zaman alır

Hesap yoksa da bir sahte doğrulayıcıya karşı hesaplama yapılıyor ve mesaj aynı.
Aksi halde giriş formu, burada kimin hesabı olduğunu öğrenmenin yolu olur.

## Sonuçlar

### Olumlu

- `AllowUnauthenticatedWrites` kaldırıldı; ADR-0013'ün §2'si yerini bu ADR'ye
  bıraktı.
- Denetim izi artık gerçek isim taşıyor — `unverified:` öneki yalnızca kimlik
  doğrulamadan önce yazılmış kayıtlarda kalıyor ve onlar da olduğu gibi okunuyor.
- Rol ayrımı var: bir viewer ortamı görebiliyor, alarm kapatamıyor.
- Dizin çökse bile ürüne girilebiliyor.
- Kurulum yüzeyi değişmedi: hâlâ tek MSI, ek bileşen yok.

### Olumsuz / kabul ettiğimiz bedel

- **Dizin entegrasyonu yok.** Yirmi kişilik bir ekip için yirmi yerel hesap
  yönetmek gerçek bir yük; AD desteği eklenene kadar bu ürünün maliyeti.
- **Hesap yönetimi ucu yok.** İlk yönetici dışında hesap eklemenin yolu henüz
  yok; bir sonraki iş.
- **Parola değiştirme, sıfırlama yok.** Parolasını unutan tek yönetici için
  bugünkü cevap "veritabanındaki satırı sil ve yeniden kur" ki kabul edilebilir
  değil ve düzeltilmeli.
- **Çok faktörlü doğrulama yok.**
- **Oturum iptali yok.** Çerez 12 saat geçerli ve bir hesabı silmek, o hesabın
  açık oturumunu anında kapatmıyor.
- **Giriş ucunda hız sınırı yok**, yalnızca hesap başına kilitlenme. Çok sayıda
  hesaba karşı dağıtık bir deneme bundan etkilenmez.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Dizin entegrasyonu eklendiğinde: rollerin grup üyeliğinden nasıl türeyeceği
  ve yerel hesapların kırılma anı rolünün nasıl korunacağı yeni bir ADR
  gerektirir.
- Toplama ile web ayrı proseslere bölündüğünde: çerez şifreleme anahtarının
  paylaşılması gerekir, ki bu bugün tek prosesin kendi anahtar halkası.
- Ürün internete açık bir yere konursa: hız sınırı, MFA ve oturum iptali
  ertelenebilir olmaktan çıkar.
