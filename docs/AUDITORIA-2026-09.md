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
| U2 | UI: textos, i18n, acessibilidade | ⏳ | — |
| U3 | UI: fluxos e usabilidade | ⏳ | — |
| B | Claude CLI / camada LLM | ⏳ | — |
| C | Estado e concorrência na UI | ⏳ | — |
| S | Serviços externos | ⏳ | — |
| P | Pequenos acertos | ⏳ | — |
| F | Funcionalidades pedidas | 📌 decidido | — |

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

## Lote U2: textos, i18n e acessibilidade ⏳

- **U2.1 Texto de privacidade falso** *(verificado)*. O Home diz "nada é enviado para servidores" mesmo com o Claude CLI, que envia o CV à Anthropic. Reescrever conforme o motor ativo.
- **U2.2 Texto fixo fora do `Loc`** *(verificado)*:
  - toggle "IA/Keywords";
  - "FONTES", "career signal", "Claude CLI" como OffContent;
  - junior/mid/senior;
  - "AI"/"KW" no mostrador;
  - título do seletor de ficheiros;
  - mensagens de exportação;
  - veredictos KW só em PT (ficam gravados na BD).
- **U2.3 Valores internos mostrados ao utilizador** *(verificado)*: chips `remote`/`hybrid`/`onsite`, a fonte `linkedin`, o nível `mid`. Mostrar rótulos traduzidos.
- **U2.4 Glossário único** *(reportado)*:
  - "Procurar vagas" só para ir buscar vagas; "Filtrar" para a pesquisa local; "Investigar" para empresas;
  - "Pontuação" em vez de score/classificar/pontuar;
  - "Instalar" para modelos.
- **U2.5 Ortografia AO90** *(reportado)*: actual → atual, seleccionado → selecionado, extracção → extração.
- **U2.6 Chave duplicada `models.none`** *(reportado)*: a mensagem amigável nunca aparece.
- **U2.7 Acessibilidade** *(verificado)*:
  - Os botões com ícone expõem o nome "Avalonia.Controls.StackPanel" a leitores de ecrã. Adicionar `AutomationProperties.Name` e tooltips nos botões só com ícone.
  - Selo "Revised" com contraste de cerca de 2,9:1.
- **U2.8 Jargão** *(reportado)*: BYOK, key-free, best-effort, tokens, reasoning, num_ctx. Mover para a secção Avançado ou reescrever.
- **U2.9 Chaves órfãs** *(verificado)*: `setup.*`, `dlg.apify.*`, `models.tab.*`, `settings.sources`…

## Lote U3: fluxos e usabilidade ⏳

- **U3.1 Estado do motor de IA no Home** *(reportado)*. Hoje, sem motor configurado, o primeiro uso acaba num erro técnico sem saída. Mostrar "Motor de IA: pronto / não encontrado" e um botão "Configurar".
- **U3.2 Edições ao perfil perdem-se** *(reportado)*. Gravar ao sair da página e pedir confirmação no "Start over" (que devia chamar-se "Descartar alterações").
- **U3.3 Página Jobs** *(verificado)*. Tem 9 botões ghost com o mesmo peso e nenhum "Procurar vagas" quando já há vagas. Pôr um CTA principal e juntar as ações secundárias (Exportar, Reclassificar, Apagar, LinkedIn) num menu "⋯".
- **U3.4 Definições** *(verificado)*:
  - 9 cartões numa só página, com o Save no fundo;
  - o Apify (pago) antes das fontes grátis;
  - o motor escolhido com um ToggleSwitch cujo rótulo diz "Claude CLI" quando está desligado;
  - o JSearch com o selo "Free" e um aviso de custos logo abaixo.

  Proposta: dividir em secções (Geral · Motor de IA · Fontes · Sistema), trocar o toggle por uma escolha explícita entre os dois motores, pôr o Avançado num Expander fechado e fixar o Save (ou gravar automaticamente).
- **U3.5 O CV Studio volta a pedir o PDF** *(reportado)*. Oferecer "Usar o CV que já carregaste".
- **U3.6 Operações longas** *(reportado)*:
  - o "Research all" não mostra progresso global nem se pode parar;
  - não há estimativa de tempo;
  - os botões de pesquisa passam a "Cancelar" no mesmo sítio, por isso um duplo clique cancela.
- **U3.7 Ações destrutivas sem confirmação** *(reportado)*: limpar conversa (Coach e CV), limpar histórico do plano, limpar importações, remover uma entrada do CV.
- **U3.8 Scroll dentro de scroll** *(reportado)*. Coach e chat do CV: a conversa deve ocupar a altura disponível, com a caixa de texto fixa em baixo.
- **U3.9 Home para quem regressa** *(reportado)*. Esconder "Usar CV de exemplo" e "Ver demonstração" quando já existe perfil.
- **U3.10 Grow** *(reportado)*. O seletor "Revisão crítica" aparece antes da ação principal, e é possível gerar um plano sem perfil.

---

## Lote B: Claude CLI / camada LLM ⏳

- **B1 Chamadas "lean" ao CLI** *(verificado e medido)*. Cada `claude -p` arranca um Claude Code completo, com CLAUDE.md, memórias, servidores MCP e ferramentas: ~28k tokens de input e 4,5 s. Com `--tools "" --strict-mcp-config --no-session-persistence --setting-sources ""` e um diretório de trabalho temporário fica em ~2,6k tokens e 3,2 s, e o texto das vagas deixa de poder dar ordens às ferramentas. O Coach precisa de `--tools Read` para ver imagens.
- **B2 `is_error` ignorado** *(verificado)*. Um envelope de erro ("usage limit…") é tratado como resposta válida. Verificar `is_error` e o exit code.
- **B3 Cancelar vira "timeout (300s)"** *(reportado, código lido)*. No caminho do CLI falta o `when (!ct.IsCancellationRequested)`, por isso Pausar/Retomar do Grow não funciona com o motor por defeito.
- **B4 `LlmClient.LastError` é estático e partilhado** *(verificado)*. Com operações em paralelo, os erros aparecem no sítio errado. Devolver o erro junto com o resultado.

## Lote C: estado e concorrência na UI ⏳ *(reportado pela revisão, a confirmar um a um)*

- **C1** O botão Research do Jobs e do Companies fica desativado enquanto corre (`[RelayCommand]` sem `AllowConcurrentExecutions`), por isso "clicar outra vez para cancelar" não funciona.
- **C2** A flag `Busy` é partilhada e não reentrante: importar um CV durante uma pesquisa volta a ativar o "Reclassificar", que lança um segundo pipeline em paralelo.
- **C3** "Ver vagas" pode duplicar cartões: o Progress e o `Dispatcher.Post` fazem um salto duplo para a thread de UI.
- **C4** CV Studio: a resposta do assistente sobrepõe o que escreveste durante a chamada, e não há gravação ao fechar a janela.
- **C5** O modo demo fica ativo a sessão toda: a pesquisa classifica como John Doe e escreve na BD real.
- **C6** `BuildCompanies` recria as VMs, por isso uma pesquisa de empresa em curso desaparece da vista.
- **C7** Carregar em Settings estando já em Settings descarta as edições sem aviso.
- **C8** O `ScrollToMaxTokensRequested` fica subscrito a cada troca de idioma (fuga de handlers).

## Lote S: serviços externos ⏳ *(verificado ao vivo em 2026-09-23)*

- **S1 O browser de modelos Ollama está partido.** O ollama.com removeu os atributos `x-test-*`, por isso a pesquisa devolve 0 resultados. Agora os cartões são `<li>` > `a[href=/library/…]`, com capacidades em spans da classe indigo e tamanhos em spans da classe blue (`ModelRegistry.cs`).
- **S2 A API do Remotive ignora `search`.** Devolve sempre as mesmas 18 vagas, por isso as queries por título são pedidos repetidos. Fazer um só pedido e filtrar localmente.
- **S3 Vagas duplicadas do LinkedIn.** A mesma vaga aparece com URLs diferentes (a Wolters Kluwer aparece 6 vezes). Normalizar a chave de dedupe tirando a query string e o tracking.
- **Ainda funcionam:** Arbeitnow, RemoteOK, Greenhouse, Jina Reader, API do Hugging Face, envelope `result` do Claude CLI 2.1.x. O Go 1.23.4 compila.

## Lote P: pequenos acertos ⏳

- `PlanText.FirstEur("€52.5k")` dá 525000 e distorce o "o que mudou" no Grow.
- O ano "2026" está fixo na query de notícias do `CompanyResearch`.
- O CSV depende da cultura ("3,9" em pt) e não protege contra fórmulas (`= + - @`).
- O Edge não é terminado quando o PDF passa do tempo limite, e o caminho não é codificado como URI. O `FindEdge` só funciona no Windows.
- O filtro não distingue "Porto" de "Porto Alegre", e um título só com o token "ai" passa como relevante.
- Tag git solta `0.2.0` (aponta para o mesmo commit que a `v0.1.0`). Nunca foi criada a tag `v0.8.0`.
- A secção "Next up" do README está desatualizada.
- `CvPdf.cs` não é usado. `IsRunning`, `Log`, `GoHome`, `CloseSettings` e `PullSuggested` não estão ligados à UI.
- O branch `sessao/2026-06-27-roadmap-local-model-catalog` só tem um commit de docs: integrar ou apagar.

---

## Lote F: funcionalidades pedidas 📌

### F1 · Pesquisar vagas no LinkedIn com Playwright 📌 *decidido pelo utilizador em 2026-09-23*
- **Pedido.** Usar o Playwright para pesquisar vagas no LinkedIn, em vez de (ou além de) colar o texto à mão, que é o import atual, e do conector pago Apify.
- **Ponto de partida.** O ROADMAP já previa esta ideia no backlog ("Playwright-assisted pass where the user logs in"). Hoje existem: o atalho que abre o LinkedIn já com a pesquisa preenchida (`MainViewModel`, cerca da linha 1545), o import por colagem com extração pela IA (`LinkedInImport.cs`, que grava `linkedin-jobs.json` e é integrado no Pipeline) e o Apify.
- **Desenho proposto (a validar antes de implementar):**
  - O utilizador faz login uma vez num browser visível, lançado pela app. A sessão fica num perfil persistente do Playwright, na pasta de dados da app, e a app nunca guarda a password.
  - A app abre a pesquisa com os títulos e a localização do perfil, percorre algumas páginas a um ritmo humano e extrai título, empresa, local, URL e descrição. O resultado vai para `linkedin-jobs.json`, por isso entra no fluxo de dedupe e scoring que já existe.
  - É opt-in e explica claramente os riscos: os Termos de Serviço do LinkedIn proíbem automação e a conta pode ser restringida. Terá limites baixos (páginas por pesquisa, intervalo entre pesquisas) e nunca corre em segundo plano sem o utilizador saber.
  - Onde corre: .NET (`Microsoft.Playwright`, com o browser descarregado quando é preciso) ou Go no fetcher. Proposta: .NET, junto ao `LinkedInImport`, porque reaproveita a UI e o merge.
  - Juntar a S3 (dedupe das vagas do LinkedIn por URL normalizado) antes ou junto com esta funcionalidade.
- **Estado.** Por desenhar e implementar, depois dos lotes de correções.
