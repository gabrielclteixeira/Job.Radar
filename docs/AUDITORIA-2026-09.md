# Auditoria Job Radar — setembro 2026



Registo vivo das correções que saíram da auditoria de 2026-09-23 (código, serviços externos e UI/UX).

Cada item tem um ID estável para ser referido em commits e conversas. Atualiza o estado quando algo muda.



**Legenda de estado**

- ✅ **Feito**: corrigido e verificado. O branch onde está vem indicado.

- 🧪 **Validado**: a correção foi testada numa cópia isolada mas ainda não está no código.

- ⏳ **Pendente**: confirmado, ainda por fazer.

- ❔ **Por confirmar**: reportado pela revisão automática, ainda não verificado à mão.



**Confirmação**

- *verificado*: vi o código, os dados reais ou o comportamento ao vivo.

- *reportado*: vem das revisões por agentes e é plausível, mas não o reproduzi.



## Resumo



| Lote | Tema | Estado | Branch |

|---|---|---|---|

| A | Integridade dos dados (core) | ✅ 16/16 feitos, por commitar | `sessao/2026-09-23-integridade-dados` |

| U1 | UI: tokens, alturas, coerência visual | ✅ 7/7 feitos, por commitar | `sessao/2026-09-23-integridade-dados` (commit separado) |

| U2 | UI: textos, i18n, acessibilidade | ✅ 9/9 feitos, por commitar | `sessao/2026-09-23-integridade-dados` |

| U3 | UI: fluxos e usabilidade | ✅ 9/10 feitos (U3.8 parcial), por commitar | `sessao/2026-09-23-integridade-dados` |

| B | Claude CLI / camada LLM | ✅ 4/4 feitos | `sessao/2026-09-23-integridade-dados` |

| C | Estado e concorrência na UI | ✅ 8/8 feitos (C1, C8 no U3 · C3 no S), por commitar | `sessao/2026-09-23-integridade-dados` |

| S | Serviços externos | ✅ S2, S3 feitos · S1 (Ollama) adiado, por commitar | `sessao/2026-09-23-integridade-dados` |

| P | Pequenos acertos | ✅ código feito · tag/README/branch no release | `sessao/2026-09-23-integridade-dados` |

| F | Funcionalidades pedidas | ✅ F1 (LinkedIn via Playwright) feito | `sessao/2026-09-23-integridade-dados` |



---



## Lote A: integridade dos dados ✅



**Branch:** `sessao/2026-09-23-integridade-dados`, ainda sem commit. Proposta de mensagem:

`fix(core): data integrity — real AI-score retries, bracket-aware profile lists, safe writes, salary ranges`.

O `fetcher-config.json` fica de fora do commit: é reescrito pela app em cada pesquisa.



**Verificação do lote**

- Build limpo (`--no-incremental`): 0 erros, os mesmos 13 avisos de antes.

- 39 testes xUnit novos passam (`dotnet test tests/JobRadar.Tests`).

- Smoke test da app numa cópia isolada dos dados.

- Impacto medido numa cópia da `radar.db` real, com o mesmo perfil: **+31 vagas relevantes e 0 perdidas**. Os 47 scores falsos são reparados.



### A1 · Scores "AI" falsos quando a IA falha ✅ *verificado*

- **Problema.** Quando o modelo não respondia (timeout, limite de uso, motor desligado, resposta ilegível), o `ScoreLoopAsync` copiava o pré-score de palavras-chave para `AiScore`. A vaga passava a mostrar o selo "AI" e, como só se classificam vagas com `AiScore == null`, nunca mais era tentada. Na tua BD havia **47 de 226** scores "AI" nesta situação (21%).

- **Correção.**

  - Uma vaga que o modelo não classificou fica com `AiScore = null`: aparece com o selo KW e é tentada na pesquisa seguinte.

  - Se um lote falha por erro do motor (ou há dois lotes vazios seguidos), o scoring pára e mostra a mensagem `scoring.aborted` no banner de erro. Antes gastava um timeout (até 300 s) por cada lote restante.

  - Ao abrir a BD, `Pipeline.OpenDbAsync` repara automaticamente as linhas antigas (`AiScore` preenchido e `AiReasons` nulo). Um resultado real grava sempre as razões, nem que seja `[]`.

- **Ficheiros.** `src/JobRadar/Pipeline.cs`, `Strings.cs` (`scoring.aborted`).

- **Verificação.** Numa cópia da BD: 47 scores falsos passaram a 0 e ficaram os 179 genuínos.



### A2 · Listas do perfil partidas nas vírgulas dentro de parênteses ✅ *verificado*

- **Problema.** O formulário do perfil partia as listas com `Split(',')`, por isso "C# / .NET (ASP.NET Core, Blazor)" virava duas skills: "C# / .NET (ASP.NET Core" e "Blazor)". O teu `profile.json` estava gravado assim, e o filtro nunca encontrava nenhuma delas.

- **Correção.**

  - `TextLists.Split` (novo): só parte em vírgulas fora de `()`, `[]` e `{}`.

  - `TextLists.RepairSplitItems`: volta a juntar listas antigas estragadas. Corre ao carregar o perfil e grava a reparação uma vez.

- **Ficheiros.** `src/JobRadar/TextLists.cs` (novo), `MainViewModel.cs` (`Split`, `RepairLists`, `LoadSavedProfile`).

- **Verificação.** Testes em `TextListsTests`. Numa cópia, o `profile.json` ficou com "C# / .NET (ASP.NET Core, Blazor)" como um só item.



### A3 · Remoto/Híbrido/Presencial não carregavam no formulário ✅ *verificado*

- **Problema.** `LoadFormFromProfile` não lia `Remote/Hybrid/Onsite`, mas `CommitFormToProfile` gravava-os. Depois de reiniciar a app, os valores por defeito (remoto e híbrido ligados, presencial desligado) substituíam as tuas escolhas na pesquisa seguinte.

- **Correção.** Os três campos passam a ser carregados no formulário.

- **Ficheiro.** `MainViewModel.cs` (`LoadFormFromProfile`).



### A4 · Carregar um CV falhado apagava o perfil guardado ✅ *verificado*

- **Problema.** Com um PDF digitalizado (sem texto) ou com a IA em baixo, `LoadCvAsync` criava um perfil vazio e **gravava-o por cima do teu `profile.json`**.

- **Correção.**

  - Em caso de falha o perfil existente é mantido e não se grava nada.

  - Se estiveres no modo demo, a app volta ao teu perfil real em vez de ficar com um perfil em branco.

  - As mensagens de erro passaram para `Loc` (`cv.noText`, `cv.aiDown`) e incluem o erro do motor.

- **Ficheiros.** `MainViewModel.cs` (`LoadCvAsync`), `Strings.cs`.



### A5 · Exclusões por substring ✅ *verificado*

- **Problema.** Os deal-breakers e as exclusões de senioridade usavam `Contains`. Por isso "intern" eliminava "International…" e "Internal Tools…", e o deal-breaker "java" eliminava todas as vagas de JavaScript.

- **Correção.** Estas comparações passam a ser por palavra inteira (`WordIn`).

- **Ficheiro.** `ProfileFilter.cs`. **Testes.** `ProfileFilterTests`.



### A6 · `.net` não era encontrado dentro de "ASP.NET" ✅ *verificado*

- **Problema.** `WordIn` exigia um separador em ambos os lados do termo. Em "asp.net" o carácter antes de ".net" é o "p", por isso nunca havia match, e um título "ASP.NET Developer" podia ser descartado como fora da stack.

- **Correção.** A fronteira só é exigida no lado em que o termo começa ou acaba com letra ou dígito ("c#" e ".net" funcionam dentro de "c#/.net" e "asp.net"). Um match rejeitado já não salta os seguintes: "gogo go" encontra "go".

- **Ficheiro.** `ProfileFilter.cs`.



### A7 · Core skills escritas como frase nunca contavam ✅ *verificado*

- **Problema.** As core skills extraídas do CV são frases, como "C# / .NET (ASP.NET Core, Blazor)" ou "AI agents & LLM integration (MCP)". O filtro procurava a frase inteira no anúncio e quase nunca a encontrava. Resultado: muitas vagas levavam a penalização "sem competência-chave" (179 das tuas relevantes), e o painel "Demand" do Grow estava quase vazio.

- **Correção.**

  - `ProfileFilter.SkillTerms` parte a frase nas suas alternativas (c#, .net, asp.net core, blazor…) e descarta partes genéricas ("integration", "development").

  - `SkillIn` considera a skill presente se qualquer alternativa aparecer no anúncio. Termos curtos ("go") só contam no título, porque "go-to" e "good" dariam falsos positivos.

  - O `JobMarket` (painel Demand e grounding do Grow) usa a mesma regra.

- **Ficheiros.** `ProfileFilter.cs`, `JobMarket.cs`.

- **Impacto.** Com o mesmo perfil, as vagas relevantes com a penalização "sem competência-chave" passaram de 179 para 80.



### A8 · Vagas guardadas nunca eram reavaliadas ✅ *verificado*

- **Problema.** Relevância, pré-score e salário eram calculados só quando a vaga entrava na BD. Mudar o perfil (stack, localização, deal-breakers), ou corrigir o filtro, não tinha efeito nas vagas antigas.

- **Correção.** Em cada pesquisa, todas as vagas guardadas são reavaliadas com o perfil atual. É barato, porque só usa palavras-chave, e os scores AI não são tocados.

- **Ficheiro.** `Pipeline.cs` (`RunAsync`, mensagem `pipe.reevaluated`).

- **Efeito que vais notar.** Na primeira pesquisa aparecem **menos vagas**: cerca de 108 só eram relevantes para versões antigas do teu perfil. O score AI delas fica guardado e voltam se o perfil voltar a mudar.



### A9 · Escritas não atómicas apagavam dados num crash ✅ *verificado*

- **Problema.** `File.WriteAllText` trunca o ficheiro antes de escrever. Um crash a meio deixava o JSON cortado, o loader engolia o erro e devolvia "vazio", e a gravação seguinte escrevia esse vazio por cima: perdia-se o CV, o histórico do Coach ou o plano.

- **Correção.**

  - `SafeFile.WriteAllText` escreve primeiro num `.tmp` e depois faz `File.Move(overwrite)`, que é atómico. É usado em todos os stores de estado: perfil, CV, chat do CV, Coach, plano, histórico, caches e settings.

  - `SafeFile.Quarantine`: quando um ficheiro não se lê, é movido para `*.corrupt-AAAAMMDD-HHMMSS` e fica registado no log, em vez de ser sobrescrito.

  - O `try` que dispara a quarentena só envolve o parse. Assim, uma falha de IO secundária nunca põe de lado um ficheiro válido.

  - `.gitignore` passou a incluir `*.tmp` e `*.corrupt-*`, porque em dev esses ficheiros aparecem na raiz do repositório.

- **Ficheiros.** `src/JobRadar/SafeFile.cs` (novo), `CvModels.cs`, `CoachHistory.cs`, `CompanyCache.cs`, `CareerPlan.cs`, `LinkedInImport.cs`, `Pipeline.cs`, `MainViewModel.cs`, `.gitignore`.



### A10 · Leitura de salários ✅ *verificado*

- **Problemas.**

  - "€40-50k" dava null e "40.000–50.000 €" só lia o máximo.

  - "€45 000" (espaço como separador) falhava e "€52.5k" dava lixo.

  - A pista "mensal" era procurada no anúncio inteiro: "6-month contract" transformava todos os valores em mensais (×12).

  - "€2,000 de orçamento de formação" virava um salário de €24k (caso real da SumUp).

- **Correção.**

  - Os intervalos são lidos com moeda e "k" partilhados; aceita espaço como separador e decimais antes de "k".

  - As pistas "mensal" e "por hora/dia" só contam até 25 caracteres do valor; as taxas horárias e diárias são ignoradas.

  - Os valores estruturados fora de €8k–€500k são descartados.

- **Ficheiro.** `SalaryParser.cs`. **Testes.** `SalaryParserTests`.

- **Casos reais.**

  - Capital on Tap "€40,000-55,000": €40k → €47,5k.

  - Speechify "30,000-65,000 USD/Year": €59,8k → €43,7k (ponto médio).

  - SumUp: €24k → sem salário.



### A11 · Salário do JSearch por hora lido como anual ✅ *verificado no código*

- **Problema.** O `job_salary_period` era ignorado, por isso "$25–35/hora" ficava "€30/ano" e levava a penalização "abaixo do mínimo".

- **Correção.** YEAR fica ×1, MONTH passa a ×12, HOUR/DAY/WEEK passam a "sem salário".

- **Ficheiro.** `JSearchClient.cs` (`AnnualFactor`).



### A12 · Parser das respostas do scorer frágil ✅ *verificado*

- **Problemas.**

  - Uma resposta truncada perdia o lote inteiro (5 vagas).

  - Um `reasons` com números rebentava a pesquisa.

  - Um `i` a começar em 0 deslocava todos os scores para a vaga errada.

  - Um score decimal (72.6) virava 0.

- **Correção.** Recupera todos os objetos completos (com um scanner que respeita strings), filtra só strings, deteta `i` base 0 e arredonda decimais.

- **Ficheiro.** `ClaudeScorer.cs` (`ParseBatch`, `TopLevelObjects`). **Testes.** `ScorerParseTests`.



### A13 · Marcador de schema gravado mesmo quando a BD não foi apagada ✅ *verificado*

- **Problema.** Se a `radar.db` antiga estava bloqueada, o delete falhava mas o marcador da versão nova era escrito na mesma. Todas as execuções seguintes davam erro de coluna em falta.

- **Correção.** Limpa o pool de ligações, apaga também `-wal`/`-shm` e, se o ficheiro continuar a existir, dá uma mensagem clara (`pipe.dbLocked`) em vez de corromper o estado.

- **Ficheiro.** `Pipeline.cs`.



### A14 · `jobs.raw.json` corrompido bloqueava todas as pesquisas ✅ *verificado*

- **Correção.** `ReadJobsFile` tolera ficheiros ilegíveis: regista o erro no log, avisa (`pipe.badFile`) e continua sem esse ficheiro.

- **Ficheiro.** `Pipeline.cs`.



### A15 · Providers `local`/`http` identificados como Claude ✅ *verificado*

- **Correção.** Criado `LlmClient.IsLocal` como definição única, usado no Pipeline, no CareerPlan e na VM.

- **Gravidade baixa:** a UI grava sempre `"openai"`, por isso só afetava quem editasse o JSON à mão.



### A16 · Projeto de testes ✅

- `tests/JobRadar.Tests` (xUnit), com `InternalsVisibleTo` no core. 39 testes.



**Ainda aberto do filtro (P):** "porto" também apanha "Porto Alegre", e um título só com o token "ai" (ex. "AI Video Editor") passa como relevante.



---



## Lote U1: coerência visual (tokens e alturas) ✅



**Branch:** `sessao/2026-09-23-integridade-dados`, ainda sem commit. Deve ir num commit separado do lote A.

Proposta de mensagem: `style(ui): one type scale, one control height, brand accent everywhere (U1)`.



**Ficheiros:** `Themes/Tokens.axaml`, `Themes/Controls.axaml`, `Views/MainWindow.axaml`, `Controls/EmptyState.axaml` e `Controls/ScoreDial.axaml`, mais uma alteração de 5 linhas em `MainViewModel.cs` (U1.7). O `MainViewModel.cs` também tem alterações do lote A: para separar os commits é preciso fazer stage por hunk.



**Verificação:**

- Build limpo (`--no-incremental`): 0 erros e os mesmos 13 avisos. Os 39 testes continuam a passar.

- Screenshots antes/depois das 8 vistas, em escuro e claro, com o harness de UI Automation numa cópia isolada.

- Métricas do `MainWindow.axaml`:



| Métrica | Antes | Depois |

|---|---|---|

| Tamanhos de fonte diferentes | 14 | 7 (só tokens) |

| `FontSize` com número fixo | 247 | **0** |

| Valores de `Spacing` | 16 | 6 |

| Valores de `Padding` | 27 | 11 |

| Valores de `CornerRadius` | 8 | 5 (3 tokens + barras finas + bolhas de chat) |

| Ícones em glifo Unicode | 18 | **0** |

| Altura na barra do Jobs (pesquisa / filtro / combo / botão) | 39 / 39 / **32** / 39 px | 36 em todos |



### U1.1 · Cor de accent da marca em todo o lado ✅ *verificado nos screenshots*

- **Problema.** No tema escuro, o botão principal aparecia lilás-claro com texto escuro, e as checkboxes e os toggles também eram lilás. A sidebar ativa usava o violeta #7C5CFF. No tema claro estava certo.

- **Causa.** O FluentAvalonia calcula as suas cores de accent a partir de `CustomAccentColor` e, no escuro, usa um tom claro. Além disso, a classe `accent` dele pinta o `ContentPresenter` do template, o que ganha ao `Background` definido no botão.

- **Correção.**

  - `Tokens.axaml`: fixar os brushes do FluentAvalonia às cores da marca, por tema: `AccentFillColor*`, `TextOnAccentFillColor*`, `CheckBoxCheckBackground*Checked` e `ToggleSwitch*On`.

  - `Controls.axaml`: cores de repouso no presenter de `Button.accent` e um estado `:disabled` explícito, para um botão desativado não parecer clicável.



### U1.2 · Uma só altura para todos os controlos ✅ *verificado (medido)*

- Token `CtrlHeight = 36`, usado como `MinHeight` em Button accent/ghost, TextBox, ComboBox, NumericUpDown e AutoCompleteBox, com o conteúdo centrado na vertical nos controlos de uma linha.

- O padding vertical dos botões baixou para 4, para que seja o `MinHeight` a definir a altura. Antes era padding + texto, o que dava 37–39 px.

- As caixas dos chats (Coach e CV) começam com 36 px e crescem até 140.

- Nova classe `Button.icon`: botão quadrado de 36×36 para as ações só com ícone (mover, remover, anexar, fechar filtro, remover modelo).



### U1.3 · Escala tipográfica fechada ✅ *verificado*

- Tokens: `FontDisplay` 38 · `FontH1` 28 · `FontH2` 20 · `FontTitle` 16 · `FontBody` 14 · `FontMeta` 13 · `FontSmall` 12 · `FontMicro` 11.

- Conversão dos valores antigos:



| Antes | Depois |

|---|---|

| 9,5 / 10 / 10,5 / 11 | Micro (11) |

| 11,5 / 12 | Small (12) |

| 12,5 / 13 / 13,5 | Meta (13) |

| 14 | Body |

| 15 / 16 | Title |

| 18 / 20 | H2 |

| 28 | H1 |

| 38 | Display |



- O mínimo passa a 11 px. Por exemplo, o rótulo "AI"/"KW" no mostrador tinha 8 px e ficou em 11.

- As classes `.fld` e `.chip` e o ScoreDial usam os tokens.



### U1.4 · Largura de página única ✅ *verificado*

- Token `PageMaxWidth = 860`. Foram removidos os `MaxWidth` próprios de Companies (760), Grow (720) e Settings (660).



### U1.5 · Espaçamentos e raios em escala ✅ *verificado*

- `Spacing` e `Padding` ajustados à escala 2 / 4 / 8 / 12 / 16 / 24. O padding dos cartões passou de 22 para 24.

- `CornerRadius` 6–7 passa a `PillRadius` e 8–10 a `CtrlRadius`. Ficam como estão as barras finas (1–3) e as bolhas de chat (4 valores).

- Os botões accent/ghost deixaram de ter padding e tamanho de fonte próprios no XAML: seguem a classe.



### U1.6 · Só `SymbolIcon` ✅ *verificado*

- Conversões: ✓ → `Checkmark`, ✕ → `Dismiss`, ○ → `Remove`, ⚠ → `Important`, → → `ChevronRight`, ★ → `StarFilled`, ↑/↓ → `ChevronUp`/`ChevronDown`.

- Os nomes foram confirmados por reflexão no FluentAvalonia 2.5.1: não existem `ArrowUp`/`ArrowDown` nem `Down`. Um nome inválido partiria o XAML compilado, e o build incremental esconde esse erro.

- O "•" nas listas fica como texto, por ser tipografia.



### U1.7 · Defeitos visuais pontuais ✅ *verificado nos screenshots*

- Título do Jobs vazio ao entrar pela sidebar: o `Navigate("results")` passa a usar `title.saved` quando não há título (`MainViewModel.cs`).

- Subtítulos cortados: a classe `.sub` passa a ter `TextWrapping=Wrap`, o que corrige o Companies e qualquer outro subtítulo.

- Faixa salarial do Grow: deixou de usar fonte mono, porque o valor pode trazer prosa.

- Barra de template do CV Studio: o combo de template ocupa 2/5 da largura e os rótulos ficam alinhados ao centro (`Margin="0"`).



**O que ficou para lotes seguintes:**

- O rótulo mais longo de template ("Panel — dark side panel (less ATS-friendly)") ainda é cortado: encurtar o texto (U2).

- O toggle do motor "Claude CLI", os valores internos `remote`/`linkedin`/`mid` e o texto "IA" fixo ficam para U2.

- A organização das Definições e o CTA do Jobs ficam para U3.



## Lote U2: textos, i18n e acessibilidade ✅

**Branch:** `sessao/2026-09-23-integridade-dados`, ainda sem commit.
Proposta de mensagem: `fix(i18n,a11y): honest privacy copy, one glossary, localized labels, accessible names (U2)`.

**Verificação:**
- Build limpo: 0 erros, 13 avisos anteriores.
- 42 testes. Os 3 novos em `StringsTests` exigem as mesmas chaves e os mesmos `{n}` em PT e EN, e nenhum valor vazio.
- Screenshots do Início, Perfil, Vagas e Empresas em PT e em EN.
- UI Automation: os botões expõem nomes reais ("Início", "Procurar vagas", …) em vez de "Avalonia.Controls.StackPanel".

### U2.1 · Texto de privacidade verdadeiro ✅ *verificado no ecrã*
- **Antes:** "Privado e local… nada é enviado para servidores". Era falso com o Claude CLI, que é o motor por defeito.
- **Agora:** "Os teus dados ficam guardados no teu computador. Com um modelo local, nada sai daqui; com o Claude CLI, o CV e as vagas são processados pela Anthropic, através da tua subscrição."
- A frase de apresentação também deixou de dizer "Tudo local… (BYOK)".

### U2.2 · Texto fixo passado para o `Loc` ✅ *verificado*
- **XAML:** toggle "IA/Keywords" (`profile.scoring.*`), "FONTES" (`common.sources`), "BASE URL" (`settings.baseUrl`).
- **VM:**
  - "N modelo(s) encontrados" (`models.found`);
  - mensagens de exportação das vagas (`export.*`);
  - "(predefinido)" (`settings.model.default`). Continua a funcionar como marcador interno em PT e EN através de `IsDefaultModel`.
- **Code-behind:** título do seletor de CV (`home.pickCv.title`).
- **Core:**
  - linhas de progresso do Pipeline (`pipe.linkedinMerged`, `pipe.addedRelevant`, `pipe.keywordMode`, `pipe.reused`, `pipe.aiOff`, `pipe.queries`, `pipe.configWarn`) e do FetcherRunner (`pipe.fetcherStartFail`, `pipe.fetcherFailed`);
  - explicação da pré-pontuação e veredicto KW do `ProfileFilter` (`filter.*`, `verdict.*`), que antes eram só PT e ficavam em PT na UI inglesa.
- **Prompt do scorer:** agora pede os veredictos, razões e alertas no idioma da UI (`ai.lang`). Antes vinham sempre em inglês.
- **Ficam propositadamente como estão:**
  - marca: "Job Radar", "career signal";
  - nomes próprios: "Claude CLI", "LM Studio", "Ollama", "APIFY API TOKEN", "ACTOR";
  - o toggle do motor, que vai ser substituído em U3.4.

### U2.3 · Rótulos legíveis em vez de valores internos ✅ *verificado no ecrã*
- Modo de trabalho: `remote`/`hybrid`/`onsite` passam a Remoto/Híbrido/Presencial (`JobVm.RemoteLabel`).
- Fonte: `linkedin`, `remoteok`, `greenhouse:feedzai` passam a LinkedIn, RemoteOK, "Greenhouse · feedzai" (`JobVm.SourceLabel`), em fonte normal. O valor bruto continua a ser usado para filtrar e pesquisar.
- Nível-alvo: `junior`/`mid`/`senior` passam a Júnior/Intermédio/Sénior. O combo usa `SeniorityIndex` e o perfil continua a guardar o valor bruto.
- Mostrador: "AI"/"KW" passam a IA/KW (`score.*`).

### U2.4 · Glossário único ✅ *verificado*
- **Procurar** = ir buscar vagas às fontes. **Filtrar** = pesquisa local ("Filtrar vagas…"). **Investigar** = empresas (Investigar empresa, Investigar todas, Investigar de novo…).
- **Pontuação** em todo o lado: Pontuação mín., Pontuar de novo, "Como é calculada a pontuação?", PONTUAÇÃO no perfil, estados e erros do scoring, pré-pontuação.
- **Instalar** para modelos (A instalar…, Instalado, Falha ao instalar). **Descarregar** para ficheiros (Ollama, atualização); a palavra "transferir" deixou de ser usada.
- "Importar" no Jobs passou a "Importar do LinkedIn".

### U2.5 · Ortografia AO90 ✅ *verificado*
- actual → atual, seleccionado → selecionado, extracção → extração, selecciona → seleciona.
- Mantêm-se as formas válidas em PT-PT: secção, facto, contacto, veredicto, impacto.

### U2.6 · Chave duplicada `models.none` ✅ *verificado*
- Removida a primeira entrada, que nunca aparecia. O texto que fica junta as duas mensagens: "Sem modelos no servidor local — está a correr no URL base? Se ainda não tens modelos, instala um abaixo."

### U2.7 · Acessibilidade ✅ *verificado com UI Automation*
- 84 botões ganharam `AutomationProperties.Name`, tirado do texto do botão ou do tooltip. Os títulos de vaga usam o próprio título.
- O botão de remover imagem do Coach, que só tinha ícone, ganhou tooltip ("Remover imagem").
- Selo "Revisto": passou de texto branco sobre verde claro (contraste ~2,9:1) para texto verde sobre fundo verde suave, como os outros chips.

### U2.8 · Menos jargão ✅ *verificado*
- Removidos da UI: BYOK, key-free, best-effort, runtime, reasoning.
- Substituições: "pesquisa web sem conta", "estimativas / não são dados oficiais", "servidor compatível com OpenAI", "modelos que “pensam” antes de responder".
- "LIMITE DE TOKENS DA RESPOSTA" passou a "TAMANHO MÁXIMO DA RESPOSTA (TOKENS)".
- `pipe.fetcherMissing` deixou de mencionar `jobs.raw.json`.
- O nome do template Panel foi encurtado para caber no combo ("Panel — painel lateral escuro (menos ATS)").

### U2.9 · Chaves órfãs removidas ✅ *verificado*
- 15 chaves removidas por par, depois de confirmar que não há referências (os prefixos dinâmicos `cv.sec.*` e `researcher.conf.*` foram excluídos): `settings.sources`, `setup.*` (exceto `setup.library`), `dlg.apify.*`, `models.tab.*`, `settings.model.hint`, `settings.downloadModel`, `settings.downloadModel.hint`, `models.suggested`, `settings.version`.
- PT e EN ficam com 603 chaves cada.

**O que fica para depois:**
- Vagas já pontuadas mantêm o veredicto da IA no idioma em que foram geradas. As próximas pontuações seguem o idioma da UI.
- A linha de contexto enviada ao modelo em `MainViewModel.cs` (~604, "vagas vistas…") é texto de prompt, não de UI.

## Lote U3: fluxos e usabilidade ✅

**Branch:** `sessao/2026-09-23-integridade-dados`, ainda sem commit.
Proposta de mensagem: `feat(ux): engine status, one primary action per page, sectioned settings, confirmations (U3)`.

**Verificação:**
- Build limpo: 0 erros, 13 avisos anteriores. 42 testes a passar.
- Screenshots em PT: Início, Vagas, Definições (as 5 páginas), Evoluir, Coach e Perfil com o diálogo aberto.
- Menu "⋯" do Jobs verificado com UI Automation: abre com Pontuar de novo · Exportar · Abrir LinkedIn Jobs · Importar do LinkedIn (24) · Apagar vagas.

### U3.1 · Estado do motor de IA no Início ✅ *verificado ao vivo*
- `LlmClient.CheckEngineAsync` confirma que o motor está utilizável sem enviar nenhum prompt:
  - Claude CLI: `claude --version` sai com código 0 (timeout de 15 s);
  - modelo local: o servidor lista modelos, incluindo o configurado.
- O resultado aparece no Início como "✓ Motor de IA pronto: Claude CLI". Se falhar, aparece um banner vermelho com o motivo e o botão **Configurar**, que abre as Definições.
- A verificação corre no arranque e depois de cada "Guardar" nas Definições.

### U3.2 · O perfil é gravado ao sair da página ✅ *verificado no código e com o diálogo*
- Sair do Perfil grava as alterações, como já acontecia no CV Studio. Antes, só "Procurar vagas" ou "Criar CV" gravavam.
- Um formulário vazio no primeiro uso não é gravado, para o Início não mudar para o modo "já tens perfil" por engano.
- "Recomeçar" passou a **"Descartar alterações"**. Pede confirmação ("Cancelar" é o botão por defeito), volta ao perfil guardado e fica na página. Antes, limpava a lista de vagas e saltava para o Início sem perguntar.

### U3.3 · Página Vagas: uma ação principal ✅ *verificado no ecrã e via UIA*
- A barra tem agora: **Procurar vagas** (accent) · contagem · "Filtrar vagas…" · Filtro · Pontuação mín. · **⋯**.
- O menu "⋯" junta Pontuar de novo, Exportar, Abrir LinkedIn Jobs, Importar do LinkedIn (com a contagem) e Apagar vagas, este último a vermelho.
- Antes eram 9 botões com o mesmo peso, em duas linhas, e não havia forma de lançar uma nova pesquisa a partir desta página.

### U3.4 · Definições organizadas ✅ *verificado no ecrã*
- Secções com título: **Geral** · **Motor de IA** · **Fontes de vagas** ("as gratuitas primeiro…") · **Sistema**.
- Botão **Guardar** também no topo da página.
- O motor passou a escolher-se com botões de opção **Claude CLI / Modelo local**. O ToggleSwitch antigo mostrava "Claude CLI" quando estava desligado.
- A secção "Avançado" do modelo local (URL base, chave de API, modelo ativo, limites) fica num Expander fechado. Abre sozinha quando o Evoluir manda o utilizador ajustar o limite de tokens.
- Ordem das fontes: Jobicy e Himalayas (sem chave) → JSearch ("Grátis com limite", aviso de **quota**) → Apify (pago).

### U3.5 · O CV Studio reutiliza o PDF do CV ✅ *verificado no código*
- O caminho do PDF usado para criar o perfil fica guardado em `ui-settings.json` (`lastCv`).
- O estado vazio do CV Studio mostra "Usar o CV que carregaste (nome.pdf)" como ação principal. "Importar de PDF…" passa a secundário.

### U3.6 · Operações longas ✅ *verificado no código*
- "Investigar todas" mostra o progresso ("A investigar 3 de 12 — Empresa…") e um botão **Parar**, que para o lote e cancela a empresa em curso. Se não houver nada por investigar, diz isso em vez de não fazer nada.
- **C1 corrigido:** os botões Investigar (vaga e empresa) passam a usar `AllowConcurrentExecutions`. Antes ficavam desativados durante a execução, por isso "clicar outra vez para cancelar" era impossível.

### U3.7 · Confirmação em ações destrutivas ✅ *verificado com o diálogo*
- Novo `ConfirmAsync`, um diálogo genérico com "Cancelar" por defeito.
- Passam a pedir confirmação: limpar a conversa do Coach, limpar o chat do CV (só quando não estão vazios), limpar o histórico de planos, limpar as importações do LinkedIn e remover uma entrada do CV (experiência, formação, projeto).

### U3.8 · Coach com a altura da janela ◐ *parcial*
- A conversa do Coach passa a ocupar a altura disponível (ajusta-se ao redimensionar e ao zoom) em vez de ficar numa caixa fixa de 460 px.
- O chat do CV continua no fundo do formulário e fica para um redesenho em duas colunas.

### U3.9 · Início para quem regressa ✅ *verificado no ecrã*
- "Usar CV de exemplo" e "Ver demonstração" só aparecem a quem ainda não tem perfil.

### U3.10 · Evoluir ✅ *verificado*
- O cartão "Gerar plano de carreira" aparece antes da "Revisão crítica".
- Sem perfil, gerar o plano dá a mensagem `improve.needProfile` em vez de gerar um plano vazio.
- O aviso "gerado por IA…" passa a aparecer sempre no idioma atual da UI (estava gravado no idioma em que o plano foi gerado).

**Extra (C8):** o handler `ScrollToMaxTokensRequested` é removido quando a janela fecha. A troca de idioma já não acumula subscrições.

**O que fica para depois:**
- Chat do CV em duas colunas (U3.8).
- O combo "Modelo" do Claude CLI aparece vazio nas Definições em vez de "(predefinido)". O problema já existia antes; confirmar se o `RefreshModelOptions` corre ao carregar.
- Ainda não há um botão "Cancelar" separado ao lado da ProgressRing da investigação por vaga (continua o clicar outra vez, que agora funciona).

## Lote B: Claude CLI ✅

**Âmbito:** só o caminho do **Claude CLI**, que é o motor que o utilizador usa. Os modelos locais ficaram de fora por decisão do utilizador (2026-09-23).
**Branch:** `sessao/2026-09-23-integridade-dados`. Proposta de mensagem: `perf(llm): lean Claude CLI calls, honour is_error, real cancel (B1–B3)`.

**Verificação:**
- Build limpo e 49 testes, 7 deles novos em `ClaudeCliTests` (argumentos e leitura do envelope).
- Consola de teste com o CLI real (2.1.x), passando pelo código da app:
  1. Chamada normal: "pong" em 3,4 s.
  2. Cancelar a 1,5 s: `OperationCanceledException`, sem o falso "timeout".
  3. Coach com imagem: lê "SALARY 55000 EUR" com `Read` limitado à pasta da imagem.
  4. Envelope `is_error` (limite de uso): tratado como erro com a mensagem, e não como resposta.
  5. CLI antigo sem as flags novas: volta sozinho aos argumentos simples e continua a funcionar.

### B1 · Chamadas "lean" ao CLI ✅ *medido e verificado*
- **Problema.** Cada `claude -p` arrancava um Claude Code completo, com CLAUDE.md, memórias, servidores MCP, ferramentas e sessão gravada.
  - Medido: ~28k tokens de input de contexto por chamada, contra ~2,6k em modo lean, e cerca de 1,2 s mais lento.
  - Um lote de pontuação (~2k tokens de prompt) passa de ~30k para ~4,6k tokens de input por chamada.
  - Além disso, o texto das vagas (não confiável) chegava a um agente com ferramentas.
- **Correção** (`LlmClient.BuildCliArgs`):
  - flags `--tools ""` (ou `--tools Read --add-dir <pasta das imagens>` no Coach com screenshots), `--strict-mcp-config`, `--no-session-persistence`, `--setting-sources ""`;
  - pasta de trabalho vazia (`%TEMP%\JobRadar-claude`), para não apanhar o CLAUDE.md nem as memórias do repositório.
- **Compatibilidade.** Se o CLI instalado não conhecer as flags ("unknown option"), a app regista um aviso, repete a chamada sem elas e memoriza a escolha para as chamadas seguintes.
- **Nota.** `--setting-sources ""` ignora os settings do utilizador no CLI. Se o modelo por defeito estiver definido lá, deve ser escolhido nas Definições da app ("Modelo").

### B2 · `is_error` respeitado ✅ *verificado*
- `LlmClient.ParseCliOutput` lê o envelope. Com `is_error: true` ou código de saída diferente de 0, a chamada falha com a mensagem. Antes, "usage limit reached" era devolvido como resposta do modelo.
- Na pontuação, isto aciona a paragem limpa do lote A1 ("A pontuação com IA parou: …").

### B3 · Cancelar é mesmo cancelar ✅ *verificado*
- O processo do CLI é terminado e a `OperationCanceledException` é relançada. Antes, o cancelamento era convertido num falso "timeout (300s)".
- Pausar/Retomar do Evoluir, Parar no Coach e no chat do CV e Cancelar nas investigações passam a funcionar com o CLI.
- `CareerPlan`: três `catch` genéricos (crítica, leitura de páginas, perguntas de aprofundamento) passaram a relançar o cancelamento, porque senão a geração continuava depois de pausada.
- O timeout próprio da chamada usa a mensagem localizada `llm.timeout`.

### B4 · `LlmClient.LastError` por operação ✅ *verificado com teste de concorrência*
- **Problema.** Era um estático partilhado. Com a pontuação, o Coach e o plano a correr ao mesmo tempo, cada um podia mostrar o erro de outra operação.
- **Correção.**
  - Cada chamada pública (`CompleteAsync`, `ChatAsync`) abre uma "caixa de erro" `AsyncLocal` no contexto de quem chama. Os métodos de entrada são deliberadamente não-`async`, para que a caixa fique visível ao chamador depois do `await`.
  - Uma leitura fora de qualquer chamada (o diagnóstico, por exemplo) devolve o último erro global.
  - O scorer guarda o erro do seu próprio lote (`LastBatchError`), porque a chamada acontece dentro de um método `async` dele.
- **Teste** (`LastErrorScopeTests`): duas chamadas em paralelo a CLIs falsos com erros diferentes, e a mais rápida só lê o erro depois de a outra falhar. **Falha com o código antigo** (confirmado com `git stash`) e passa com o novo.

## Lote C: estado e concorrência na UI ✅

**Branch:** `sessao/2026-09-23-integridade-dados`. Proposta de mensagem: `fix(ui-state): busy counter, no lost CV edits, contained demo profile, stable company VMs (C)`.

**Verificação:**
- Build limpo e 58 testes.
- Com a app real, via UI Automation, numa pasta isolada:
  - **gravar ao fechar:** escrevi no nome do CV, fechei a janela normalmente (`CloseMainWindow`) e `cv-data.json` ficou com o texto. O ficheiro original foi reposto depois;
  - **perfil de exemplo:** aparece o aviso e "Voltar ao meu perfil" repõe o perfil;
  - **largura das páginas:** Perfil vazio, Definições e Empresas acabam todos no mesmo píxel.

- ✅ **C1** (no U3.6) O botão Investigar ficava desativado enquanto corria, por isso "clicar outra vez para cancelar" não funcionava.
- ✅ **C2** A flag `Busy` passou a ser um contador (`BeginBusy`/`EndBusy`, 14 pares). Uma operação curta que termina (importar um CV, exportar, listar modelos…) já não reativa "Procurar / Pontuar de novo" a meio de uma pontuação. `RunPipeline`, `Rescore`, `ViewJobs` e `ResumeScoring` recusam-se a arrancar se já houver uma pontuação a correr.
- ✅ **C3** (no lote S) Cada vaga aparecia duas vezes na lista.
- ✅ **C4** Edições do CV que se perdiam:
  - se escreves ou importas enquanto o assistente responde, a resposta **não é aplicada** e aparece a nota `cv.chat.editedMeanwhile`. Antes, sobrepunha as tuas edições;
  - "Começar a partir do perfil" e "Importar" fazem commit dos editores antes do snapshot de anular;
  - ao fechar a janela, o CV e o perfil abertos são gravados (`SaveOnExit`).
- ✅ **C5** Perfil de exemplo contido:
  - aparece um aviso no Início e no Perfil, com o botão **Voltar ao meu perfil**;
  - uma pesquisa com o perfil de exemplo usa só palavras-chave, para não gravar pontuações de IA do John Doe na tua BD.
- ✅ **C6** `BuildCompanies` reutiliza os `CompanyVm` existentes (`JobCount` passou a ser atualizável). Uma investigação em curso já não desaparece ao voltar à página.
- ✅ **C7** Carregar outra vez na página onde já estás (Definições ou Perfil) já não recarrega o formulário, que descartava as edições.
- ✅ **C8** (no U3) Fuga de handlers na troca de idioma. Também o novo `PropertyChanged` do zoom é removido ao fechar a janela.
- ✅ **Extra: plano de carreira.** Se a regeneração falhar, o plano anterior volta ao ecrã (antes desaparecia até reiniciar a app), com o erro num banner junto às ações do plano.
- ✅ **Extra: largura das páginas (complemento do U1.4).** A coluna de conteúdo encolhia com o conteúdo; o Perfil vazio ficava mais estreito. Agora tem sempre `PageMaxWidth`, ou a largura disponível em janelas pequenas, e é recalculada com o redimensionar e com o zoom.

## Lote S: serviços externos ✅ (S2, S3) · S1 adiado

**Branch:** `sessao/2026-09-23-integridade-dados`. Proposta de mensagem: `fix(sources): canonical job identity + DB dedupe, one Remotive call, no double-listed jobs (S2, S3, C3)`.

**Verificação:**
- Build limpo e 58 testes, 9 deles novos em `JobKeysTests`, com URLs reais da BD.
- Numa cópia da BD real, pelo caminho `LoadCachedAsync`: **1640 → 1508 linhas**, **269 → 203 relevantes**. Wolters Kluwer ficou com 1 linha (antes 3, que no ecrã eram 6). Deloitte ficou com 2 (Porto e Braga, legítimas).
- No ecrã, a página Vagas mostra "203 de 203", sem cartões repetidos. Antes mostrava "537 de 537", com cada vaga duas vezes.
- Fetcher Go recompilado (`fetcher.exe` na raiz) e executado: um só pedido ao Remotive, 396 vagas únicas.

### S1 · Browser de modelos do Ollama ⏸ adiado
- Continua partido: o ollama.com removeu os atributos `x-test-*`. Fica fora porque o utilizador pediu para não investir no LLM local (2026-09-23). O seletor para quando for retomado está no lote S original: `<li>` > `a[href=/library/…]`, spans indigo para capacidades e blue para tamanhos.

### S2 · Remotive: um só pedido ✅ *verificado ao vivo*
- A API pública ignora `search`, `category` e `limit`, e devolve sempre as ~19 vagas mais recentes.
- O fetcher fazia um pedido por título de pesquisa, com o mesmo resultado. Agora faz um único pedido por execução, e a relevância é decidida pelo filtro do perfil.
- **Limitação real:** o Remotive gratuito só dá cerca de 19 vagas por pesquisa.

### S3 · Identidade das vagas e dedupe ✅ *verificado*
- **Causa.** A chave de dedupe era o URL completo. O LinkedIn acrescenta `position=…&refId=…`, diferentes em cada pesquisa, por isso a mesma vaga voltava a ser inserida a cada execução. As republicações com outro ID eram a outra metade do ruído.
- **Correção** (`JobKeys`, novo):
  - `CanonicalUrl`: no LinkedIn fica `linkedin.com/jobs/view/<id>`; nos outros sites saem os parâmetros de tracking (utm, ref, position…) e ficam os que identificam a vaga (ex.: `gh_jid` do Greenhouse).
  - `FuzzyKey`: título + empresa + cidade. "Porto, Porto, Portugal" e "Porto, Portugal (Híbrido)" contam como a mesma cidade; Porto e Braga continuam diferentes.
  - Ao inserir, uma vaga já existente com outro URL é ignorada e o log mostra "N vagas repetidas ignoradas".
  - Ao abrir a BD, os duplicados antigos são removidos. Fica a linha em que o utilizador mexeu (status), depois a pontuada com o score mais alto, depois a de descrição mais completa, depois a mais recente.

### C3 · Cada vaga aparecia duas vezes na lista ✅ *verificado no ecrã*
- **Causa.** O `Progress` já entrega os eventos na thread da UI, e cada evento fazia ainda um segundo `Dispatcher.Post`. Assim, os `AddStreamed` corriam depois do `FinalizeResults`, que já tinha montado a lista a partir do resultado, e cada vaga entrava duas vezes.
- **Correção.** O `Progress` passou a chamar `AddStreamed` diretamente (em 4 sítios), e o `AddStreamed` ignora vagas que já estão na lista (mesma linha ou mesma chave).

## Lote P: pequenos acertos ✅ (código) · o resto fica para o release

**Verificação:** build limpo e 71 testes, 13 deles novos em `SmallFixesTests`. Os filtros foram confrontados com uma cópia da BD real: a regra nova só retirou a vaga de Porto Alegre.

- ✅ **FirstEur:** "€52.5k" dava 525 000 e distorcia o "o que mudou" do Evoluir. Agora os decimais antes de "k" são lidos corretamente.
- ✅ **Ano da pesquisa de notícias:** usa o ano atual em vez de "2026" fixo.
- ✅ **CSV** (`CsvText`, novo):
  - os números são escritos sempre com ponto; em pt-PT, "3,9" partia a coluna;
  - as células começadas por `= + - @` levam um `'` à frente, o que impede a injeção de fórmulas vinda dos títulos das vagas;
  - vale para as vagas e para as empresas.
- ✅ **PDF:**
  - o browser headless é terminado se passar dos 30 s;
  - o caminho vai como URI (espaços, `#`, `%`);
  - `FindEdge` passou a procurar Edge, Chrome ou Chromium no Windows, no macOS e no Linux (PATH). Antes, os builds de macOS e Linux caíam sempre para HTML.
- ✅ **Filtro:**
  - "Porto" já não conta como "Porto Alegre" nem outros nomes parecidos (`AmbiguousPlaces`);
  - um título que só coincide num token curto ("ai", "ui") sem nenhuma competência do perfil já não passa como relevante (ex.: "AI Cinematic Video Editor"). O "Go" e o "C#" continuam a contar através das competências.
- ✅ **Código morto removido:** `CvPdf.cs` (o renderizador antigo, sem referências) e os comandos `GoHome`/`CloseSettings`, que não estavam ligados à interface.
- ⏳ **No release** (mexem em git, no remoto ou em docs públicas):
  - apagar a tag solta `0.2.0`;
  - decidir o que fazer ao branch `sessao/2026-06-27-roadmap-local-model-catalog`, que só tem um commit de docs sobre o catálogo de modelos locais e fica em espera, tal como o LLM local;
  - atualizar o README ("Next up" desatualizado) e o ROADMAP com tudo o que foi feito.

---



## Lote F: funcionalidades pedidas ✅

### F1 · Pesquisar vagas no LinkedIn com Playwright ✅ *verificado ao vivo e na app*

- **Pedido.** Usar o Playwright para pesquisar vagas no LinkedIn, além do import por colagem e do conector pago Apify.

- **Decisões do utilizador (2026-09-23):**
  - o componente Playwright **instala-se a partir das Definições**, para o instalador da app não crescer;
  - uma **área de opções nas Definições** para limitar e afinar a pesquisa.

- **Desenho final (mudou em relação à proposta):**
  - **Sem login.** Em vez de usar a conta do utilizador, a app lê só as páginas públicas de vagas do LinkedIn (os mesmos endpoints que o site usa para quem não tem sessão). A conta nunca é tocada, por isso não pode ser restringida. O aviso sobre os Termos continua visível no cartão.
  - **Browser real.** O Playwright conduz o Edge/Chrome/Chromium que já está instalado (`FindEdge`). Se não houver nenhum, há um botão para instalar o Chromium do Playwright (~150 MB).
  - **Instalação a pedido** (`LinkedInBrowser.InstallAsync`):
    - descarrega o Node.js `v24.18.1` (nodejs.org, verificado por SHA-256) e o `playwright-core 1.62.0` (npm, verificado por sha512), cerca de 40 MB;
    - monta a estrutura `.playwright/{package,node}` na pasta de dados da app e aponta o `PLAYWRIGHT_DRIVER_SEARCH_PATH` para lá;
    - a extração tem proteção contra *zip-slip*;
    - `Directory.Build.props` define `PlaywrightPlatform=none`, por isso o build e o release não incluem o driver;
    - os ZIPs de driver do CDN da Microsoft dão 404 para esta versão, daí as fontes oficiais nodejs.org e npm.
  - **Pesquisa** (`FetchJobsAsync`): percorre os primeiros N títulos do perfil na localização do perfil, 10 vagas por página, com pausas aleatórias à volta do ritmo escolhido. Lê a descrição de cada vaga, se essa opção estiver ligada. Para ao receber HTTP 429/999 ou a página de login e guarda o que já tinha. O URL de cada vaga é o canónico `linkedin.com/jobs/view/<id>`, por isso o dedupe do S3 funciona.
  - **Pipeline:** corre depois do import por colagem e antes do Apify. Uma falha do browser é registada e a pesquisa segue com as outras fontes; cancelar cancela mesmo.

- **Definições → cartão "LINKEDIN (BROWSER)":**
  - estado do componente e do browser, com os botões Instalar, Instalar Chromium, Testar agora e Remover, e a linha de progresso;
  - interruptor, desativado até o componente estar instalado;
  - opções: máximo de vagas por título (20/50/75/100), número de títulos (1–4), data de publicação (qualquer/24 h/semana/mês), ritmo (~2/4/8 s), ler descrições, mostrar o browser;
  - as opções ficam em `linkedin-browser-settings.json` (no `.gitignore`), como as outras definições locais.

- **Verificação:**
  - 4 testes novos em `LinkedInBrowserTests`: parsing dos cartões e da descrição, com fixtures capturadas do site real; construção do URL; e a versão do pacote igual à do driver descarregado (se alguém atualizar o NuGet sem atualizar o driver, o teste falha);
  - ao vivo: 20 vagas .NET reais no Porto, todas com descrição, em 56 s, com o Edge do utilizador;
  - na app: "Testar agora" deu "Teste: 10 vagas encontradas." (screenshot);
  - build limpo (sem avisos novos) e 76/76 testes.

- **Limites conhecidos:** o LinkedIn pode mudar o HTML das páginas públicas. Se isso acontecer, o parser deixa de encontrar vagas (0 resultados, sem crash) e os fixtures têm de ser atualizados.
