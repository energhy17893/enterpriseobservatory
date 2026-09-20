# Tasarım: Kural motoru — ve onu şimdi inşa etmeme gerekçesi

**Durum: tasarım incelemesi.** Sonuç, çoğu tasarım belgesinin vardığından
farklı: **motoru bugün inşa etme, ama açığı bugün kapat.**

Bu belge [analiz katmanı önerisinin](analysis-layer.md) devamı ve onu iki
yerden düzeltiyor.

---

## 1. Gereksinimler — yazılmış kurallardan çıkarıldı

Öneri §9 şunu söylemişti: *"iki kural somut olarak yaz, ortak olanı üçüncüde
çıkar."* Üç kural yazıldı. Ne oldukları önemli:

| Kural | Nerede durdu | Girdi | Çıktı | İhtiyacı |
|---|---|---|---|---|
| **R3** Arıza sayaçları | `Application/Analysis` | O turun gözlemleri | `AlertDefinition[]` | Yok |
| **R2** Akran aykırılığı | `Application/Analysis` | O turun gözlemleri | `AlertDefinition[]` | Politika (2 eşik) |
| **R1** Ölçülemezlik | **`Collectors.Vsphere`** | O turun gözlemleri | `CollectionFailure` | vSphere sayaç adları |

### İlk bulgu: tetik koşulu aslında sağlanmadı

R1 motorun kapsamına **girmedi** — grafiğe, geçmişe, ikinci nesneye ihtiyacı
yok, ve yalnızca toplayıcının bilebileceği bir şey. Kendi koyduğumuz sınır onu
başka yere yerleştirdi ve bu doğruydu.

Yani elimizde **üç değil iki** motor-kapsamı kural var. "Üçüncüde çıkar"
kuralına göre **henüz sırası gelmedi.**

### İşlevsel gereksinimler (iki kuraldan gözlemlenen)

1. Girdi: `IReadOnlyList<Observation>` — o turun toplu hâli
2. Çıktı: `IReadOnlyList<AlertDefinition>`
3. Saf fonksiyon: durum yok, yan etki yok, saat yok
4. Bazıları politika alır (eşikler), bazıları almaz
5. Gruplama anahtarı kurala göre değişir — R3 gözlem başına, R2
   `(entity, counter)` başına

### İşlevsel olmayan gereksinimler

| | Ölçülen / gerekli |
|---|---|
| Girdi hacmi | Tur başına **6 084 gözlem**, 30 saniyede bir |
| Karmaşıklık | R3 O(n), R2 O(n log n) — n=6 084'te ikisi de önemsiz |
| Gecikme bütçesi | Tur 30 sn; kurallar toplamda ≪ 1 sn |
| **Hata izolasyonu** | **Yok** — aşağıya bakın |

---

## 2. Asıl açık: kurallar izole değil

Hata modeli ölçüldü, tahmin edilmedi:

| Bileşen | İzolasyon | Nerede |
|---|---|---|
| Toplayıcı kaynakları | ✅ Kaynak başına yakalanır, veriye dönüşür | `SourceRunner` |
| Depolama yazma | ✅ `_lastStorageFailure` olarak üründe görünür | `MonitoringCycle` |
| **Kurallar** | ❌ **Hiç yok** | — |

Bugün fırlatan bir kural `MonitoringWorker`'a kadar çıkıyor, orada
`CycleFailed` olarak **loglanıyor** ve tur atlanıyor. Kaybedilenler:

- O turun **bütün** toplama alarmları (yalnızca kuralınkiler değil)
- O turun bildirimleri
- Ve tek kanıt sunucudaki bir dosyada

Bu, ürünün kendi ilkesini ihlal ediyor. `_lastStorageFailure`'ın yorumu
şöyle diyor: *"toplayıp saklamıyoruz" sunucudaki bir dosyada değil, üründe
görünür olsun.* Aynı cümle kurallar için de geçerli.

**Ve bu açık motor olmadan da kapatılabilir.**

---

## 3. İki seçenek

### A. Bugün: sarmala, motor kurma

```csharp
var observed = cycle.CollectionAlerts
    .Concat(Guarded("fault-counters", () => FaultCounters.Evaluate(cycle.Observations)))
    .Concat(Guarded("peer-outliers", () => PeerOutliers.Evaluate(cycle.Observations, options.PeerOutliers)))
    .ToList();
```

`Guarded`, `SourceRunner`'ın kaynaklar için yaptığını kurallar için yapar:
istisnayı yakalar, **veriye dönüştürür** (bir `CollectionFailure` ya da tur
sonucunda bir alan), ve diğer kuralların çalışmasına izin verir.

| | |
|---|---|
| Maliyet | ~20 satır, bir yardımcı, iki test |
| Kazanç | Asıl açığın tamamı |
| Kaybedilen | Kural başına aç/kapa, kural başına zamanlama |

### B. Motoru şimdi kur

```
IAnalysisRule { Id, Evaluate(AnalysisContext) }
AnalysisEngine — kayıt, izolasyon, politika, zamanlama
```

| | |
|---|---|
| Maliyet | Arayüz, bağlam tipi, kayıt, DI, politika modeli, ~6-8 test |
| Kazanç | İzolasyon **+** kural başına aç/kapa **+** okuma bütçesi yeri |
| Risk | **İki örnekten soyutlama.** Üçüncü motor-kapsamı kural, arayüzü değiştirebilir |

---

## 4. Öneri: A, ve B'nin tetiğini yaz

**Bugün A.** Sebep, kolaycılık değil, kanıt:

1. **Tetik sağlanmadı.** İki motor-kapsamı kural var, üç değil.
2. **Asıl acil şey izolasyon**, ve onun motorla ilgisi yok.
3. **Üçüncü kural arayüzü değiştirecek gibi duruyor.** Bugünkü ikisi de yalnızca
   o turun toplu hâlini okuyor. Sıradaki aday kuralların en az ikisi
   *geçmişe* bakıyor — "üç haftadır tırmanıyor", "her salı artıyor". O gün
   `AnalysisContext` bir `ISeriesReader` ve bir okuma bütçesi taşımak zorunda
   kalacak, ve bugün yazılacak arayüz onu barındırmayacak.

Bir soyutlamayı, onu haklı çıkaracak örnekleri görmeden çıkarmak,
[ADR-0017](../adr/0017-retention-windows-set-by-measurement.md)'de günlük
kademe için verilen kararın aynısı — ve bu projede aynı gerekçe üçüncü kez
işliyor.

### B'nin tetiği (yazılı, ki unutulmasın)

Motor şu **ikisinden biri** olduğunda çıkarılır:

1. **Üçüncü motor-kapsamı kural** yazıldığında — o zaman ortak şekil üç
   örnekten çıkar, ikiden değil.
2. **Geçmişe bakan ilk kural** yazıldığında — çünkü o, bağlamı ve okuma
   bütçesini zorunlu kılar, ve ikisi motorun asıl gerekçesidir.

İkincisi muhtemelen önce gelir.

---

## 5. Motor geldiğinde: şekli, bugünden görülebildiği kadarıyla

Bu bölüm inşa talimatı değil; o günkü tasarımın **başlangıç noktası.**

```
            MonitoringCycle
                  │
          AnalysisEngine
                  │
     ┌────────────┼────────────┐
  FaultCounters  PeerOutliers  (geçmişe bakan kural)
                                     │
                               ISeriesReader  ← okuma bütçeli
```

```csharp
public interface IAnalysisRule
{
    string Id { get; }                       // "storage.peer-outlier"
    IEnumerable<AlertDefinition> Evaluate(AnalysisContext context);
}

public sealed record AnalysisContext
{
    IReadOnlyList<Observation> Observations { get; init; }
    DateTimeOffset NowUtc { get; init; }
    ISeriesReader Series { get; init; }      // bütçeli, dar
}
```

### O gün karara bağlanacaklar

| Konu | Neden şimdi karar verilmiyor |
|---|---|
| Okuma bütçesi biçimi | Hangi okuma desenlerinin gerektiğini bilmiyoruz |
| Kural başına aç/kapa nerede saklanır | Politika modeli yok; `MonitoringOptions` yeterli olabilir |
| Symptom/alert bileşimi (vROps modeli) | Henüz çok-koşullu tek bir kural yok |
| Kural başına zamanlama/teşhis | Henüz yavaş bir kural yok |

**Symptom/alert ayrımı özellikle ertelenmeli.** [Referans
yaklaşımlar §3](../reference-approaches.md) onu benimsemeye değer buldu — ama
gerekçesi çapraz metrik kurallarıydı ("kuyruk derinliği 64 **ve** bekleyen IO
200"), ve öyle bir kural henüz yazılmadı. Bileşim katmanını, bileşilecek bir
şey olmadan kurmak aynı hatanın üçüncü biçimi olur.

---

## 6. Ne değişmiyor

Bu tasarım incelemesi şunları **doğruladı**, değiştirmedi:

- **Sınır** (toplayıcı gözlemi raporlar, motor yargıyı verir) R1'de sınandı ve
  tuttu — R1'i doğru yere yolladı.
- **Vendor-nötrlük yöntemi** iki kez işledi: `IsFaultCount` ve
  `InstanceIsVantagePoint`. Toplayıcı anlamı bildirir, kural sayaç adı bilmez.
  Üçüncü kural da muhtemelen üçüncü bir bayrak getirecek, ve bu bir desen
  olduğunun işareti — `CounterValue` şişerse orada bir `CounterSemantics`
  tipi çıkar.
- **Alarm makinesi** (histerezis, flap, scope, Silence) yeniden kullanıldı ve
  yetti. Ayrı bir `Finding` tipine gerek olmadığı, R2/R3 canlıda çalıştıktan
  sonra da doğru görünüyor.

---

## 7. Gözden geçirilecekler (sistem büyüdükçe)

- **6 084 gözlem/tur** bugün önemsiz. Estate on katına çıkarsa R2'nin
  gruplaması (O(n log n)) hâlâ önemsiz, ama kural sayısı × gözlem sayısı
  çarpımı ölçülmeli — bugün ölçülmedi çünkü ölçülecek bir şey yok.
- **İki kaynak** olduğunda: kurallar şu an tüm gözlemleri tek küme görüyor.
  İki vCenter'ın datastore'ları akran sayılmamalı — R2'nin gruplaması
  `Entity`'ye göre olduğu için bugün doğru çalışır, ama bu şans eseri değil
  de tasarım olduğu **yazılmalı ve test edilmeli**.
- **Kural bir alarm fırlatırsa** ne olacağı (A seçeneğiyle) bir
  `CollectionFailure` olur. Kural sayısı artınca bu liste gürültüye dönebilir;
  o noktada kural başına sağlık kaydı gerekir — yani motorun bir parçası daha.
