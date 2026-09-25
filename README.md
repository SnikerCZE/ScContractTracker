# SC Contract Tracker

Desktopová aplikace pro Windows, která ve hře **Star Citizen** sleduje tvoje přijaté kontrakty,
měří, jak dlouho ti trvají, a počítá **výdělek v aUEC za hodinu**.

*A Windows companion app for Star Citizen that tracks your accepted contracts, their duration,
rewards and aUEC per hour. Interface in English, Czech and German.*

> Neoficiální fanouškovský nástroj bez vazby na Cloud Imperium Games.
> Aplikace nic nemění ve hře, nečte její paměť a nic do hry neposílá.

## Co umí

- **Automaticky zachytí kontrakty** – přijetí, dokončení, selhání, opuštění i ukončení při odhlášení, s přesným časem.
- **Přehled aktivních kontraktů** – kompaktní karty s typem kontraktu, zadavatelem, odměnou, časem přijetí
  a **aktuálním úkolem** (co a kde udělat). Po splnění úkolu se hned ukáže další. Kartu jde rozbalit na všechny detaily.
- **Odměna, zadavatel, popis a úkoly** se načtou z mobiGlasu, když otevřeš *Contracts › Accepted*.
- **Poplatek za přijetí** a **čistý výdělek** (odměna − poplatek).
- **3 nejbližší místa** ke každému kontraktu a co tam udělat.
- **Historie** – barevně podle výsledku, seskupení podle typu / zadavatele / názvu, rozbalení na celou kartu,
  úpravy, mazání, filtr období.
- **Statistiky** – počet kontraktů, úspěšnost, výdělek, průměr a medián odměny, doby, **aUEC/h** a nejlepší kontrakty,
  celkem i po skupinách.
- **Blueprinty a předměty** – přehled všeho, co jsi za kontrakty získal, s hledáním.
- **Ruční zadání a úprava** kontraktu, včetně všech údajů.
- **Režim dokončení AUTO / MANU** – kontrakt se ukončí sám podle hry, nebo až tlačítkem v aplikaci.
- **Import starší historie** z předchozích herních sezení.
- **Tabulky podle sebe** – zobrazení/skrytí sloupců, šířka a pořadí (pravé tlačítko na hlavičce).
- **Automatické aktualizace** – nová verze se stáhne sama.

## Instalace

1. Stáhni **`ScContractTracker-win-Setup.exe`** z [Releases](../../releases) a spusť ho.
   Instalace nepotřebuje práva administrátora.
2. Při prvním spuštění zkontroluj v *Nastavení › Předvolby* cestu ke hře
   (výchozí `C:\Program Files\Roberts Space Industries\StarCitizen\LIVE`) a jazyk hry.
3. Hraj. Aplikaci můžeš mít otevřenou vedle hry nebo na druhém monitoru.

**Požadavky:** Windows 10 (verze 2004 a novější) nebo Windows 11.

## Tipy pro používání

- **Odměna a popis kontraktu:** po přijetí kontraktu otevři v mobiGlasu *Contracts › Accepted* a klikni na kontrakt.
  Aplikace si údaje přečte sama. Ručně to spustíš přes *Nápověda › Přečíst mobiGlas*.
- **Režim zobrazení hry:** funguje okno, Borderless i Fullscreen. Kdyby aplikace ve stavovém řádku hlásila černý obraz,
  stiskni ve hře na záložce Accepted **PrtScn** – aplikace si snímek obrazovky přečte.
- **Rozpoznávání textu:** aplikace používá rozpoznávání textu z Windows. Pokud chybí pro jazyk hry, aplikace ho
  doinstaluje sama (Windows se jednou zeptá na potvrzení). Později ho spustíš z *Nastavení › Nainstalovat OCR pro jazyk hry*.
- **Poloha pro nejbližší místa:** napiš do chatu ve hře `/showlocation` – aplikace souřadnice převezme ze schránky.
- **Starší kontrakty:** *Soubor › Import historie z logbackups…* načte kontrakty, blueprinty a předměty z předchozích sezení.
- **Pád hry:** přijaté kontrakty zůstávají aktivní (stejně jako ve hře). Odhlášení nebo ukončení hry přes menu je ukončí.
- **Když aplikace čte špatné hodnoty:** *Nápověda › Uložit diagnostiku OCR* a pošli uložené soubory v [Issues](../../issues).

## Jazyky

V *Nastavení* nastavíš zvlášť **jazyk aplikace** a **jazyk hry** (podle jazyka, ve kterém hraješ).
Vestavěná je angličtina, čeština a němčina.

## Data a soukromí

Vše zůstává jen na tvém počítači ve složce `%AppData%\ScContractTracker` (historie, nastavení).
Aplikace nic neodesílá; připojuje se jen ke GitHubu kvůli kontrole nových verzí (lze vypnout v nastavení).

## Chyby a nápady

Nahlas je v sekci [Issues](../../issues).

## Licence

MIT © 2026 Jiří Libich.
Star Citizen® je ochranná známka Cloud Imperium Rights LLC. Tento projekt není spojen s Cloud Imperium Games.
