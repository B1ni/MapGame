# Map Game

Jednoduchá zemepisná hra v slovenčine. Po nasadení na Render Free beží verejne cez HTTPS: kamarátom stačí webový odkaz, prehliadač a internet, nie rovnaká Wi-Fi, stiahnutie hry ani hráčsky účet. Lokálne spustenie a Windows balík naďalej podporujú pôvodný offline/LAN režim.

## Verejná hra na Render Free (GitHub + Docker)

Zdrojový kód je pripravený, **nie je však automaticky nahraný ani nasadený**. Potrebuješ vlastný GitHub a Render účet. Docker na svojom PC nepotrebuješ: obraz zostaví Render.

1. Vytvor na GitHube repozitár a nahraj zdroje z tohto priečinka. V koreňovom priečinku repozitára musia byť `Dockerfile`, `.dockerignore`, `render.yaml` a priečinok `MapGame` s projektom a jeho `wwwroot`. Nahraj aj testy, riešenie a README. Nezdieľaj `FullGame`, `Logs`, `bin`, `obj`, `Balik-na-zdielanie` ani Windows ZIP (pri Git nahrávaní ich vylučuje `.gitignore`; pri ručnom nahrávaní ich vynechaj sám).
2. Na [Render Dashboard](https://dashboard.render.com/) sa prihlás a zvoľ **New → Web Service → Git Provider**. Pripoj GitHub a vyber svoj repozitár.
3. Zvoľ región **Frankfurt**, jazyk/runtime **Docker**, Dockerfile `./Dockerfile`, kontext zostavenia koreň repozitára (`.`); **Root Directory nechaj prázdny**. Nezadávaj vlastný Build Command ani Docker Command: zostavenie a štart určuje Dockerfile.
4. Vyber výslovne **Free** (nie Starter ani iný platený plán). V Advanced nastav **Health Check Path** na `/health`. Environment:

   | Premenná | Hodnota |
   | --- | --- |
   | `Hosting__Mode` | `Internet` |
   | `Hosting__FileLogs` | `false` |
   | `ASPNETCORE_ENVIRONMENT` | `Production` |

   `PORT` dodáva Render (štandardne `10000`). Verejnú HTTPS adresu berie aplikácia z Render premennej `RENDER_EXTERNAL_URL`; **nevkladaj sem LAN adresu**. Pre vlastnú doménu nastav `Hosting__PublicOrigin` na jej presný HTTPS origin, napr. `https://hra.example.sk`, bez cesty alebo parametrov. Povolený je iba jeden origin: používaj ho aj pri otváraní stránky. Ďalšie domény/aliasy nie sú automaticky povolené.
5. Klikni **Create Web Service** a počkaj na úspešný build/deploy. Alternatíva: **New → Blueprint**, vyber ten istý repozitár a použi dodaný `render.yaml` (definuje práve jednu Free Docker web službu, bez databázy a disku). Aj tam skontroluj, že plán je Free.
6. Otvor svoju skutočnú adresu `https://<nazov-sluzby>.onrender.com` z Dashboardu. Vytvor lobby, skopíruj webový odkaz a otvor ho na inom zariadení, pokojne cez mobilné dáta. Každý zadá iné meno; až potom hostiteľ spustí zápas. Adresa hostiteľa ani výber sieťového adaptéra sa v online režime nezobrazujú. WebSocket používa **wss** na tom istom verejnom origine.

**Bez platobnej karty:** Free služba môže fungovať bez pridania platobnej metódy; neaktivuj platené doplnky, disk ani databázu. Pri vyčerpaní kvót bez platobnej metódy sa služby/buildy pozastavia namiesto plateného navýšenia. Render môže podľa svojich aktuálnych podmienok vyžadovať overenie účtu; aplikácia to nemôže ovplyvniť ani garantovať registráciu bez karty každému. Ak účet ponúka iba platený variant alebo žiada kartu, nepokračuj automaticky na platený plán.

### Skutočné obmedzenia bezplatného hostingu

- Po **15 minútach bez prichádzajúcich HTTP požiadaviek alebo WebSocket správ** Render server uspí. Prebudenie trvá približne minútu. Pri prvom otvorení počkaj na Render loading stránke; herný JavaScript sa spustí až po načítaní aplikácie. Online pripojenie má 90-sekundový limit, nie krátky LAN limit. Neúspešný pokus zopakuj po prebudení.
- Free workspace má **750 hodín za kalendárny mesiac**, spoločných pre Free služby. Existujú aj mesačné limity prenosu dát a build minút; aktuálne hodnoty sleduj v Dashboard → Billing. Free nie je záruka nepretržitej dostupnosti ani produkčné SLA.
- Render môže službu reštartovať; aj deploy, uspatie alebo pád **zmaže všetky lobby, rozbehnuté zápasy, výsledky a singleplayer relácie**. Po výpadku vytvor novú hru. Opätovné pripojenie do starého zápasu nie je podporované. Hostiteľ musí zostať v prehliadači pripojený; jeho odpojením sa aktívna spoločná hra zatvorí.
- Stav je iba v pamäti jednej inštancie. **Databáza ani persistentný disk nie sú potrebné**; neškáluj na viac inštancií bez prerobenia zdieľania stavu. CPU/RAM sú obmedzené, preto je to hobby hra, nie veľký verejný herný server.
- Ochrany aplikácie: najviac **32 lobby, 16 hráčov v lobby, 128 WebSocket spojení a 256 singleplayer relácií**. Lobby vyprší po 30 minútach; výsledky po 15 minútach. Singleplayer relácia zanikne po 30 minútach bez API aktivity alebo po 15 minútach od skončenia. Absolútna životnosť relácie/spojenia je 4 hodiny. Údržba prebieha každú minútu a ukončí aj časový zápas bez klientského pollingu.
- HTTP telá a WebSocket správy majú limit 4 KB; spojenie bez kompletnej správy 2 minúty sa zatvorí (čakáreň posiela priebežný sync). WebSocket povoľuje najviac 60 správ za 10 sekúnd na spojenie. API a nové WebSocket handshaky spolu majú limit 3000 požiadaviek/minútu na službu; pri prekročení vracajú HTTP 429. Plná singleplayer kapacita vracia 503. Ide o základné obmedzenia, nie úplnú ochranu proti DDoS.

### Hosting a diagnostika

HTML, JavaScript a CSS vyžadujú pred opätovným použitím overenie aktuálnosti (`Cache-Control: no-cache`). Ak po nasadení vidíš staré ovládanie, obnov stránku cez Ctrl+F5. Výber Normal/Hardcore sa zobrazuje po zvolení Map Battle aj pri obnovení predchádzajúcej voľby prehliadačom.

Render ukončuje TLS pred kontajnerom; kontajner počúva na HTTP `0.0.0.0:$PORT`. Aplikácia overí host aj presný HTTPS WebSocket origin podľa konfigurácie, **nedôveruje ľubovoľným `X-Forwarded-*` hlavičkám** a nepovoľuje na internete localhost/LAN klientské originy. `/health` je verejná jednoduchá sonda bez herných dát, dostupná aj s interným hostom zdravotnej kontroly.

Na Render sú technické logy v **Dashboard → služba → Logs** (stdout/stderr). Súborové logy sú v Docker režime vypnuté; ak ich zapneš, sú len na efemérnom disku a pri reštarte/deployi sa stratia. Žiadne hráčske mená, tipy ani odpovede sa do serverových logov nezapisujú. Server neukladá voľný text klientských diagnostických udalostí; plný klientský technický report zostáva dostupný cez **Stiahnuť diagnostiku pripojenia** v prehliadači.

Pred zdieľaním verejnej URL over `/health`, vytvorenie lobby, pripojenie z druhého zariadenia, spustenie zápasu, súkromné vyfarbovanie mapy a výsledky až po skončení. GitHub/Render účty a tento finálny verejný smoke test sú kroky vlastníka služby.

Zdroje podmienok: [Render Free](https://render.com/docs/free), [Docker deploy](https://render.com/docs/docker), [Web Services](https://render.com/docs/web-services). Limity a registračné podmienky sa môžu meniť.

## Spustenie pri vývoji

Potrebný je .NET 10 SDK:

```powershell
dotnet run --project MapGame\MapGame.csproj -- --no-browser
```

Predvolené lokálne nastavenie je `Hosting:Mode=Lan` (nie je určené na internet). Otvor `http://localhost:5080`. Pre hru cez LAN otvor hostiteľovu adresu `http://<IP-adresa>:5080`, ktorú aplikácia vypíše do konzoly, a povoľ aplikácii prístup v súkromnej sieti, ak sa na to Windows opýta. Hostiteľ vytvorí hru a môže pozvať ostatných dvoma spôsobmi:

- **Bez stiahnutia hry:** hráči otvoria webový odkaz z čakárne v prehliadači.
- **So stiahnutou hrou:** hráči spustia `MapGame.exe`, hra sa automaticky otvorí v prehliadači a zadajú adresu hostiteľa a šesťmiestne ID lobby. Hostiteľovu adresu nájdu v čakárni alebo v konzole hostiteľskej aplikácie.

V oboch prípadoch musia byť zariadenia pripojené k rovnakej Wi-Fi/LAN sieti. Hostiteľ musí nechať svoju aplikáciu spustenú počas hry.

Každý hráč vrátane hostiteľa zadá **vlastné meno v multiplayeri**; v jednej čakárni musia byť mená odlišné. Čakáreň sa otvorí až po potvrdení serverom a všetkým zobrazuje mená aktuálne pripojených hráčov.

Ak má hostiteľ viac sieťových adaptérov, v čakárni vyberie v poli **Sieť hostiteľa** adresu Wi-Fi/LAN, ku ktorej sú pripojení aj ostatní. Odkaz a adresa pre stiahnutých klientov sa aktualizujú podľa výberu. Adresy s bránou majú prednosť, no pri VPN alebo viacerých aktívnych sieťach treba skontrolovať správnu adresu. Stiahnutý klient môže zadať aj samotnú IPv4 adresu; aplikácia doplní `http://` a port `5080`.

Ak sa webový odkaz vôbec neotvorí, skontrolujte povolenie `MapGame.exe` vo Windows Firewall pre **súkromnú sieť** a vypnite izoláciu zariadení na hosťovskej Wi-Fi. Hra automaticky nemení firewall. Pri neexistujúcom ID alebo neúspešnom spojení zobrazí chybu namiesto prázdnej čakárne. Pri prerušení spojenia počas hry zobrazí upozornenie priamo na hernej obrazovke.

## Windows aplikácia bez inštalácie

Spusti v koreňovom priečinku repozitára:

```powershell
.\Publish-Windows.ps1
```

Výsledná aplikácia je v priečinku **`FullGame`** v koreňovom priečinku repozitára. Skript sem pri každom spustení publikuje aktuálnu samostatnú Windows x64 verziu vrátane .NET runtime, máp, webového rozhrania, README a diagnostických/testovacích skriptov. Pred aktualizovaním balíka zatvor jeho bežiacu hru. `FullGame` je v `.gitignore` a nie je súčasťou Git commitu ani Render obrazu; po zmene zdrojov ho obnov spustením `Publish-Windows.ps1`.

**Pre hráča:** skopíruj celý `FullGame` a spusti v ňom `MapGame.exe`. Na singleplayer netreba internet, Render ani inštaláciu .NET; potrebuješ iba Windows x64 a prehliadač. Po pripravení lokálneho servera sa automaticky otvorí `http://localhost:5080` v predvolenom prehliadači. Konzolové okno zobrazuje živé logy s časmi udalostí a musí zostať otvorené počas hry; jeho zatvorením sa server ukončí. Rovnaké udalosti sa ukladajú do priečinka `Logs`. Ak sa prehliadač nepodarí spustiť, konzola vypíše chybu a adresu na ručné otvorenie. Pre vývoj alebo automatizáciu možno automatické otvorenie vypnúť argumentom `--no-browser`.

Pri kopírovaní alebo zdieľaní zachovaj celý priečinok vrátane `wwwroot` a `Data`. Na počítači hráča netreba inštalovať .NET. Hráči bez stiahnutej hry môžu hrať v prehliadači cez pozývací odkaz; hráči so stiahnutou hrou sa pripájajú z aplikácie pomocou adresy hostiteľa a ID.

## Herné režimy

- **Na čas:** vyber mapu alebo náhodný región a limit od 1 do 120 minút.
- **Na životy:** vyber mapu alebo náhodný región a 1 až 5 životov. Nesprávny alebo opakovaný tip odoberie život.
- **Multiplayer:** hostiteľ zvolí mapu a časový limit. V online aj LAN hre je počas celého zápasu nad mapou priebežný rebríček všetkých hráčov s počtom uhádnutých štátov, obnovovaný každú sekundu. Poradie určuje počet odpovedí a pri zhode čas dosiahnutia skóre; rovnaké pravidlá platia pre konečné výsledky. Každý hráč na svojej mape naďalej vidí iba vlastné uhádnuté štáty.
- **Map Battle (online aj LAN multiplayer):** hostiteľ zvolí **Herný režim → Map Battle** a variant **Normal** alebo **Hardcore** ešte pred spustením zápasu. Každý hráč má vlastnú farbu zobrazenú pri mene v rebríčku; všetci vidia tú istú mapu s farbami aktuálnych vlastníkov. Každý vlastnený štát je jeden bod. V **Normal** štát získa prvý hráč a ďalší ho už obsadiť nemôžu. V **Hardcore** správna odpoveď ukradne súperov štát: súper stratí bod a nový vlastník ho získa. Štát možno kradnúť opakovane; vlastný štát ani jeho alias nepridá ďalší bod.

Map Battle skončí **uplynutím času alebo ihneď po obsadení posledného voľného štátu**, aj v Hardcore (potom už kradnutie nepokračuje). Vyhrá najvyšší počet vlastnených štátov; rovnosť bodov znamená remízu, čas nerozhoduje. Hostiteľ môže hru ukončiť predčasne. Odídenému/odpojenému hosťovi zostávajú jeho štáty a skóre; v Hardcore ich možno ukradnúť, v Normal zostávajú obsadené. Odpojenie hostiteľa zatvorí hru rovnako ako v klasickej súťaži. Klasická súťaž a singleplayer zachovávajú pôvodné pravidlá.

V singleplayeri sa počas hry počet uhádnutých štátov nezobrazuje. Správne uhádnuté štáty sa na mape zafarbia nazeleno. Mapa zaberá väčšiu časť obrazovky a dá sa posúvať potiahnutím; priblíženie a oddialenie funguje kolieskom myši alebo tlačidlami +/−. Tlačidlo s percentami vráti pôvodný pohľad.

### Názvy a odpovede (slovenčina aj angličtina)

Všetky krajiny aj 50 štátov USA majú slovenský a anglický názov. **Oba jazyky prijíma hra súčasne**, bez prepínania a bez nutnosti diakritiky; rozhranie zostáva slovenské. Prijímajú sa aj ISO alpha-2/alpha-3 kódy krajín (napr. `SK`, `SVK`), americké poštové skratky (`CA`, `NY`) a vybrané bežné alternatívy (`Turkey`, `UAE`, `JAR`). Skratky sa vyhodnocujú iba v zvolenej mape. Nejednoznačné aliasy sa neprijímajú (napr. samotné slovenské `Kongo`). Iný jazyk alebo alias už uhádnutej krajiny je opakovaný tip, nie ďalší bod; v režime na životy odoberie život.

V Európe sú namiesto jedného Spojeného kráľovstva **štyri samostatné odpovede s vlastnými hranicami**:

| Slovensky | English | Bežný kód | ISO 3166-2 / stabilné ID |
| --- | --- | --- | --- |
| Anglicko | England | ENG | GB-ENG |
| Škótsko | Scotland | SCO (aj SCT) | GB-SCT |
| Wales | Wales | WAL (aj WLS) | GB-WLS |
| Severné Írsko | Northern Ireland | NIR | GB-NIR |

`ENG`, `SCO`, `WAL`, `NIR` sú zámerne bežné/športové skratky, nie ISO kódy samostatných suverénnych štátov. `UK`, `GB`, `GBR`, `Británia`, `Great Britain` ani `United Kingdom` neuhádnu žiadnu z nich. `Anglicko` vyfarbí iba Anglicko, nie celé Spojené kráľovstvo. Európa má 48 odpovedí; ostatné mapy zostávajú na 46 (Ázia), 22 (Severná Amerika), 12 (Južná Amerika), 54 (Afrika) a 50 (USA).

Tlačidlo **Ukončiť hru** umožní predčasne skončiť a zobraziť výsledok. V multiplayeri môže zápas pre všetkých ukončiť hostiteľ; ostatní hráči môžu iba opustiť zápas.

## Testy

### Ručný multiplayer test na jednom PC

V rozbalenom Windows balíku dvakrát klikni na **Test-Multiplayer.cmd** (potrebný je Edge alebo Chrome). Najprv zatvor už spustenú hru. Test spustí jeden server s konzolou/logmi a postupne tri okná s oddelenými, čistými profilmi:

- **Hostiteľ:** zadaj meno a vytvor lobby; jej webový odkaz vlož do testovacieho konzolového okna. Bez aktívnej siete môžeš vložiť samotné ID; test použije lokálny odkaz `127.0.0.1`.
- **Hráč cez odkaz:** druhé okno sa otvorí s ID z pozvánky. Zadaj odlišné meno a pripoj sa bez vypĺňania adresy.
- **Simulovaný stiahnutý klient:** tretie okno otvorí lokálne rozhranie; zadaj ďalšie meno, adresu hostiteľa a ID vypísané testom.

V čakárni over tri mená, spusti zápas, skús odpovede v každom okne a porovnaj výsledky. Po skončení zatvor tri testovacie okná a stlač Enter v testovacej konzole. Skript ukončí iba server, ktorý sám spustil, a odstráni testovacie profily, ak už nie sú otvorené. Bežné profily prehliadača nemení.

**Nejde o virtuálne počítače:** všetci klienti bežia na jednom PC. Tretí klient používa lokálne mapy/rozhranie z toho istého servera, ale pripája vlastný WebSocket cez zadanú adresu. Test overí oddelené hráčske spojenia a hernú logiku, nie dostupnosť medzi počítačmi, firemnú izoláciu Wi-Fi ani pravidlá vzdialeného firewallu. Na to je naďalej potrebný `Test-LAN.ps1` na druhom PC alebo IT-schválená sieť.

### Automatické regresné testy

```powershell
dotnet test MapGame.slnx -c Release -p:UseSharedCompilation=false
node --check MapGame\wwwroot\app.js
node --check MapGame\wwwroot\diagnostics.js
node --test MapGame.Tests\MultiplayerClientTests.cjs MapGame.Tests\BrowserDiagnosticsTests.cjs
```

Testy overia pravidlá a skryté skóre, troch WebSocket hráčov, limity a expiráciu, súborové logovanie aj skutočný štart aplikácie v internetovom režime (host/origin kontroly, nedôveryhodné forwarded hlavičky, statické dáta, veľkosť HTTP tela a WebSocket lobby). Node testy pokrývajú pôvodné LAN pozvánky aj online WSS/HTTPS pozvánky a export diagnostiky. Test internetového servera lokálne simuluje TLS termináciu Render; nenahrádza verejný test po deployi.

Katalógové testy navyše kontrolujú všetkých 232 odpovedí, oba jazyky, kódy, aliasy a duplicity, presnú zhodu verejnej/serverovej geometrie a neprístupnosť súboru s odpoveďami. Geografické testy overujú samostatné britské hranice, známe vnútrozemské body, ostrovy, spoločné hraničné úseky (aj s Írskom) a neprítomnosť plošných prekryvov.

Voliteľný test v **skutočnom Chromium prehliadači** (Node 22+, bez npm závislostí): spusti lokálny server a samostatný Chrome/Edge s `--remote-debugging-port=9229` a `--user-data-dir=<nový priečinok v projekte>` (nie svoj bežný profil); potom `node MapGame.Tests\BrowserMapSmoke.cjs`. Premenné `MAPGAME_URL` a `MAPGAME_CDP` môžu zmeniť predvolené `http://localhost:5080` a `http://127.0.0.1:9229`. Test otvorí a zatvorí vlastné karty, overí štyri rôzne SVG obrysy, singleplayer aj multiplayer pozvánku, jednotlivé vyfarbenie, aliasy, odmietnutie UK, súkromné klasické mapy aj spoločné Battle farby, kradnutie, remízu a koniec po obsadení celej mapy. Po teste zatvor iba tento testovací prehliadač a odstráň jeho profil; debugging port nevystavuj do internetu.

Ak máš Docker, môžeš navyše zostaviť a spustiť rovnaký obraz:

```powershell
docker build -t map-game .
docker run --rm -p 10000:10000 -e Hosting__PublicOrigin=https://localhost:10000 map-game
```

Kontajner sám neposkytuje TLS. Pre skutočné lokálne HTTPS/WSS prehliadačové testovanie potrebuješ TLS reverse proxy a zodpovedajúci `Hosting__PublicOrigin`; samotné otvorenie `https://localhost:10000` proti tomuto HTTP kontajneru nebude fungovať. Jednoduchá HTTP sonda funguje cez `http://localhost:10000/health`.

## Diagnostika pripojenia

V **lokálnom/LAN režime** aplikácia automaticky zapisuje logy do priečinka **Logs** vedľa hry (pri chýbajúcom práve na zápis do `%LOCALAPPDATA%\MapGame\Logs`; presnú cestu vypíše v konzole). Každé spustenie má vlastné súbory `MapGame-<čas UTC>-<proces>-<časť>.jsonl`. Log sa priebežne zapisuje aj pred zatvorením aplikácie; pre jedno spustenie sa uchovávajú najviac tri časti po približne 5 MB.

Log zaznamená sieťové adaptéry, adresy/brány, prichádzajúce HTTP požiadavky, stavové kódy a dôvod zamietnutia, prijatie/odmietnutie WebSocketu, ID spojenia, vytvorenie a pripojenie lobby, štart hry a odpojenie. Nezapisuje tipy ani hráčske mená. Obsahuje miestne IP adresy, ID lobby a technické identifikátory; pred odoslaním ho môžeš skontrolovať.

Ak kamarát otvorí stránku, ale pripojenie zlyhá, klikne dole na **Stiahnuť diagnostiku pripojenia**. Súbor `MapGame-klient-*.json` obsahuje klientské kroky a chyby (aj keď sa ich nepodarilo odoslať serveru). Server zaznamená iba prijatie diagnostiky bez jej textových polí. WebSocket technické udalosti má server hostiteľa (v online režime Render). Diagnostické požiadavky idú iba na server, ktorý poskytol stránku; nepoužíva sa ďalšia analytická služba. Report môže obsahovať adresy a ID lobby, preto ho pred zdieľaním skontroluj.

Ak sa stránka **vôbec neotvorí**, prehliadač nemôže spustiť jej diagnostiku a hostiteľ nemusí dostať ani jednu požiadavku. Na Render najprv počkaj na prebudenie a skontroluj stav služby/Logs v Dashboarde. **Pre LAN** môže kamarát dostať samotný `Test-LAN.ps1` a spustiť ho v PowerShelli:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-LAN.ps1 -HostAddress http://192.168.1.25:5080
```

Skript nemení firewall ani sieťové nastavenia. Zapíše TCP/HTTP test a lokálne sieťové profily do `Logs\Test-LAN-*.txt`. Na vyšetrovanie pošli hostiteľské logy z času pokusu, klientský JSON alebo výstup skriptu a čas pokusu. Samotná neprítomnosť požiadavky v hostiteľskom logu nerozlíši firewall, chybnú adresu alebo izolovanú Wi-Fi; TCP/HTTP test to pomôže zúžiť.

## Mapové dáta a licencie

Hranice štátov pochádzajú z [Natural Earth](https://www.naturalearthdata.com/) a sú vo verejnej doméne. Slovenské názvy a metadáta krajín pochádzajú z [mledoze/countries](https://github.com/mledoze/countries) a databáza je šírená pod [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/). Licenčné texty a zdroje sú súčasťou aplikácie v `MapGame\wwwroot\data`.

Nové hranice štyroch krajín Spojeného kráľovstva sú presné geometrie z **Natural Earth v5.1.2, Admin 0 – Map Subunits, 1:50m** (public domain). Nejde o obdĺžniky ani štyri kópie obrysu GBR. Zdrojové identifikátory, URL a SHA-256 sú v `NATURAL-EARTH-SOURCES.txt`. Severoírska hranica používa rovnaké súradnice ako existujúce Írsko; všetkých 228 ostatných geometrií ostalo nezmenených. Hranice sú kartograficky generalizované, nie geodetické.

### Úprava katalógu bez súradníc

- `MapGame\Data\country-names.json`: serverový katalóg podľa stabilných ID; `regions` a `features` obsahujú `names.sk` / `names.en` s `name` a voliteľnými `aliases`, `features` navyše `codes`. Tu upravuj názvy, skratky a aliasy bez otvárania súradníc. Obsah je vložený do serverovej assembly, nie sprístupnený ako statický súbor.
- `MapGame\Data\maps.json`: iba ID regiónov/prvkov a geometria. Loader overí presnú zhodu ID s katalógom a prítomnosť oboch jazykov; chýbajúci alebo osirelý záznam zastaví načítanie namiesto tichého vynechania odpovede.
- `GameRegion.GetName("sk"/"en")` a `MapFeature.GetName("sk"/"en")` poskytujú lokalizovaný zobrazovaný názov (nepodporovaný jazyk sa vráti na slovenčinu). Singleplayer aj multiplayer používajú jeden index všetkých jazykov s odfiltrovaním nejednoznačných aliasov.
- `MapGame\wwwroot\data\map-shapes.json`: verejná geometria. Po zmene geometrie alebo názvu regiónu ju obnov štandardným Pythonom: `python tools\refresh-map-shapes.py`. Prepínač `--import-uk` opätovne stiahne a overí pripnutý Natural Earth zdroj a prevezme iba štyri britské geometrie.
- Po úprave dát treba server znovu zostaviť/nasadiť. Pre Render commitni a pushni zdroje z tohto repozitára (vrátane oboch súborov v `Data`) na vetvu sledovanú Renderom; pri zapnutom Auto-Deploy sa nasadia automaticky. Lokálny balík obnov cez `Publish-Windows.ps1`; `FullGame` necommituj. Žiadne tajomstvá ani ďalšia služba nie sú potrebné.

Mapa zobrazená v prehliadači obsahuje iba geometriu a identifikátory. Odpovede a aliasy zostávajú v serverovej časti hry.
