# ADR-0002: ADR ve commit disiplini, dil seçimi

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001

## Bağlam

Bu ürünün önceki sürümü 2–18 Eylül 2026 arasında **~130 MSI sürümü** üretti
(v1.4.0 → v4.3.58). Bazı gecelerde 5-10 build çıktı. Ürün adı yolda iki kez
değişti. Bu tempo gerçek bir başarıdır — fikirden çalışan yazılıma geçiş süresi
çok kısaydı.

Ancak **versiyon kontrolü yoktu.** Sonuçları:

- 46 bin satırlık çekirdekte hiçbir değişikliğin ne zaman, neden girdiği
  bilinmiyor.
- İki MSI arasındaki fark alınamıyor; bir regresyonun hangi sürümde girdiği
  bulunamıyor.
- Kararların gerekçeleri `docs/` altındaki 316 KB + 148 KB'lık sohbet
  transkriptlerinde ve geliştiricinin hafızasında.
- Kurumsal müşteriye kaynak kod emaneti (escrow) verilemez.
- İkinci bir geliştirici projeye katılamaz.

Yani sorun kod kalitesi değildi — kod iyiydi, 505 test geçiyordu. Sorun,
**kodun taşıdığı bilginin kodla birlikte seyahat etmemesiydi.**

Bu yeni projede aynı hatayı yapmamak için hızdan ödün vermeden disiplin
kurmamız gerekiyor.

## Karar

Üç katmanlı bir kayıt disiplini uygula:

**1. Commit seviyesi — ne değişti.**
[Conventional Commits](https://www.conventionalcommits.org/) formatı zorunlu,
`commit-msg` hook'uyla makine tarafından doğrulanır. Sürüm numaraları ve
CHANGELOG bu commit'lerden türetilir; elle yazılmaz.

```
<tip>(<kapsam>): <özet>

<gövde — NEDEN yapıldı, ne değiştiğini değil>

<altbilgi>
```

Gövde, *ne* yapıldığını anlatmaz (diff zaten söylüyor); **neden** yapıldığını ve
hangi alternatifin neden seçilmediğini anlatır.

**2. Karar seviyesi — neden böyle.**
Mimari kararlar `docs/adr/` altında numaralı ADR'ler olarak yaşar. Kurallar
[docs/adr/README.md](README.md) içinde.

**3. Sürüm seviyesi — ne yayınlandı.**
Anlamsal sürümleme (SemVer), git etiketlerinden MinVer ile türetilir. Her
yayınlanan artefakt bir commit'e geri izlenebilir.

**Dil:** Kod, kod yorumları, commit mesajları ve API adları **İngilizce**.
ADR'ler, CONTRIBUTING ve ürün dokümantasyonu **Türkçe**.

## Gerekçe

**Neden Conventional Commits?** Sürüm ve CHANGELOG üretimini otomatikleştirir,
yani disiplin *işe yarar* — sadece bürokrasi olmaz. Bir kuralın sürdürülebilir
olması için karşılığında bir şey vermesi gerekir.

**Neden hook ile zorlama?** ADR-0001'deki aynı mantık: insan disiplini gevşer.
Tek geliştiricili bir projede "ben dikkat ederim" altı hafta sürer. Makine
kontrolü süresizdir.

**Neden commit gövdesinde "neden"?** Önceki projenin tam olarak kaybettiği şey
buydu. Kod ne yaptığını söyler, git diff ne değiştiğini söyler; neden yapıldığını
söyleyen tek yer commit gövdesi ve ADR'dir.

**Neden karma dil?** İki farklı okuyucu var:

- *Kod* — gelecekteki geliştiriciler, muhtemel dış katkıcılar, araçlar. .NET
  ekosisteminin tamamı İngilizce; `MusteriKimlik` gibi isimler standart
  kütüphanelerle yan yana durduğunda okunabilirliği düşürür.
- *ADR ve ürün dokümanı* — bugün sen, yarın Türk kurumsal müşteri ve iş ortağı.
  Bir kararın gerekçesini en net ifade edebildiğin dilde yazman, o kararın
  kalitesini doğrudan etkiler. Mimari muhakemeyi ikinci dilde yapmak fikri
  yoksullaştırır.

## Değerlendirilen alternatifler

### Alternatif A: Serbest commit mesajı, ADR yok

Hızlı. Önceki projenin yaklaşımı.

Seçilmedi çünkü bu projenin var olma sebebi tam olarak bu yaklaşımın ürettiği
sonuç. Aynı şeyi tekrar yapmak için yeni bir repo açmaya gerek yok.

### Alternatif B: Her şey İngilizce

Uluslararası katkıya en açık seçenek.

Seçilmedi çünkü bugünkü tek geliştirici ve hedef pazar Türkçe. ADR'nin değeri
düşüncenin netliğinde; onu zorlaştıran bir kısıt, ADR'lerin yüzeyselleşmesine
yol açar. Ürün uluslararasılaşırsa ADR'ler çevrilebilir — kod zaten İngilizce
olduğu için asıl maliyet düşük kalır.

### Alternatif C: Her şey Türkçe

Seçilmedi çünkü .NET tip sistemiyle karışık dil, kod okunabilirliğini kalıcı
olarak düşürür ve hiçbir dış kütüphane/araç Türkçe tanımlayıcı beklemiyor.

## Sonuçlar

### Olumlu

- Her satırın gerekçesi commit veya ADR olarak bulunabilir.
- CHANGELOG ve sürüm numarası elle bakım gerektirmez.
- Kod tabanı devredilebilir: yeni bir geliştirici ADR'leri okuyarak mimari
  muhakemeyi öğrenir.
- Kurumsal denetim ve kaynak kod emaneti mümkün hale gelir.

### Olumsuz / kabul ettiğimiz bedel

- **Her commit daha yavaş.** Format düşünmek ve "neden" yazmak gerçek bir
  ek yük. Önceki projenin gece 5 build temposu bu disiplinle mümkün olmaz.
- ADR yazmak, kararı yazmadan önce netleştirmeyi zorlar — bu genelde iyidir
  ama acele bir karar için sürtünmedir.
- Karma dil, bazı sınır durumlarda tutarsızlık üretir (örn. Türkçe bir ADR
  İngilizce tip adlarına atıf yapar). Kabul edilebilir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Projeye Türkçe bilmeyen bir geliştirici katılırsa dil kararı yeniden
  değerlendirilmeli.
- Commit disiplini pratikte ilerlemeyi ölçülebilir şekilde yavaşlatıyorsa,
  hook'un katılığı (tip listesi, uzunluk sınırları) gevşetilebilir — ama
  "neden" gövdesi korunmalı.
