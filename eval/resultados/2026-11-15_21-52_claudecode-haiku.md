# Avaliação do RAG

- **Data:** 15/11/2026 21:52
- **LLM:** ClaudeCode (haiku) · também usado como juiz
- **Embeddings:** Ollama (nomic-embed-text)
- **Banco vetorial:** InMemory
- **Parâmetros:** TopK 4 · MinScore 0,35 · Chunk 800/150
- **Perguntas:** 24 (20 com resposta nos documentos, 4 sem)

## Resumo

| Métrica | Resultado | O que mede |
|---|---|---|
| Busca | **100% (20/20)** | o documento certo veio entre os trechos encontrados |
| Respostas corretas | **95% (19/20)** | a resposta bate com o gabarito |
| Fidelidade | **95% (19/20)** | não inventou nada além dos trechos |
| Recusa correta | **100% (4/4)** | disse "não encontrei" quando a resposta não existe |
| Recusa indevida | **0 de 20** | disse "não encontrei" mas a resposta existia |
| Tempo por pergunta | **5,9 s (máx. 7,7 s)** | busca + IA (+ reescrita, quando há histórico) |

## Pergunta a pergunta

| Pergunta | Busca | Resultado | Fiel | Tempo | Observação |
|---|:-:|---|:-:|--:|---|
| Qual o prazo para trocar um produto? | ✅ | Correta | ✅ | 7,7 s | Resposta precisa: traz a informação central do gabarito (30 dias corridos com cupom/número do pedido) e adiciona detalhes corretos (7 dias para eletrônicos, 90 dias para defeitos) totalmente apoiados nos trechos. |
| Comprei um fone de ouvido e me arrependi. Até quando posso trocar? | ✅ | Correta | ✅ | 6,8 s | A resposta extrai corretamente do documento que eletrônicos têm 7 dias para troca por arrependimento, com todas as condições apoiadas nos trechos fornecidos. |
| Qual o prazo para devolver um produto com defeito de fabricação? | ✅ | Correta | ✅ | 5,6 s | A resposta traz exatamente os dois pontos do gabarito (90 dias + análise em 5 dias úteis) e todas as informações adicionais estão suportadas pelos trechos [1] e [2]. |
| Posso trocar uma peça íntima que não serviu? | ✅ | Correta | ✅ | 6,7 s | Resposta alinhada ao gabarito e completamente apoiada no trecho [1], com informações adicionais sobre procedimentos de devolução por defeito. |
| Paguei com PIX. Em quanto tempo recebo o estorno? | ✅ | Correta | ✅ | 6,0 s | A resposta reproduz fielmente o trecho [2], trazendo os dois elementos principais do gabarito: prazo de 2 dias úteis e depósito na mesma conta de origem. |
| Quando o estorno no cartão de crédito aparece? | ✅ | Correta | ✅ | 4,8 s | A resposta reproduz exatamente a informação do Trecho [1], que afirma 'O estorno no cartão de crédito aparece em até duas faturas'. Nenhuma informação é inventada. |
| Como funciona a devolução de uma compra paga em dinheiro? | ✅ | Correta | ✅ | 6,5 s | A resposta reproduz corretamente a informação do gabarito (crédito na loja ou devolução no caixa com autorização do gerente) e todos os detalhes adicionados estão verificados nos trechos [1] e [3] fornecidos. |
| Qual o valor padrão do fundo de troco na abertura do caixa? | ✅ | Correta | ✅ | 5,0 s | A resposta identifica corretamente o valor (R$ 200,00) e o respalda com citação direta e fiel do trecho [1]. |
| Como lanço várias unidades do mesmo produto no PDV? | ✅ | Correta | ✅ | 5,2 s | A resposta segue exatamente o gabarito (quantidade → asterisco → código), todos os passos estão apoiados no trecho [2], e o detalhe sobre 'leitor de código de barras ou digitando' complementa com informação do mesmo trecho. |
| Como cancelo um item antes de finalizar a venda? | ✅ | Correta | ✅ | 4,3 s | A resposta apresenta corretamente os dois elementos do gabarito (F6 + seleção do item; senha do supervisor para valores >R$100) e tudo está apoiado no trecho [2]. |
| Em quantas vezes posso parcelar no cartão de crédito? | ✅ | Correta | ✅ | 5,1 s | A resposta reproduz corretamente a informação do trecho [1], citando as 6 parcelas sem juros e a condição de compra acima de R$ 300,00, sem adicionar ou omitir detalhes relevantes. |
| Dá para pagar uma mesma compra com mais de uma forma de pagamento? | ✅ | Correta | ✅ | 5,6 s | Resposta confirma o gabarito corretamente (até 3 formas diferentes) e lista apenas as formas mencionadas no trecho [1], sem adicionar informações inventadas. |
| Quando preciso fazer sangria do caixa? | ✅ | Correta | ✅ | 4,7 s | Resposta completa e precisa: menciona o limite de R$ 1.000,00, o papel do supervisor na retirada e registro com senha, tudo conforme o trecho [3] do manual. |
| Como faço o fechamento do caixa no fim do turno? | ✅ | Correta | ✅ | 5,4 s | A resposta abrange todos os elementos do gabarito (F10, valores contados, relatório, justificativa para diferenças acima de R$ 5,00) e está totalmente apoiada no trecho [3] do manual. |
| A impressora fiscal parou de imprimir, o que eu faço? | ✅ | Correta | ✅ | 5,3 s | A resposta cobre todos os passos do gabarito (verificar ligação/papel, testar em Configurações > Periféricos, reiniciar com Ctrl+Shift+P) e está totalmente alinhada com o trecho [1] fornecido. |
| O sistema caiu a internet. Consigo continuar vendendo? | ✅ | Correta | ❌ | 6,2 s | A resposta acerta sobre modo contingência e indisponibilidade do PIX (nos trechos), mas afirmar que 'apenas' dinheiro, débito e crédito funcionam é uma inferência não explicitamente apoiada pelos trechos. |
| Como altero o preço de um produto? | ✅ | Correta | ✅ | 6,7 s | Resposta contém todos os elementos do gabarito (retaguarda, menu Produtos > Tabela de preços, 10 minutos e Sincronizar agora) com estrutura clara e totalmente apoiada no trecho [4]. |
| Qual o horário de atendimento do suporte técnico? | ✅ | Correta | ✅ | 5,2 s | A resposta reproduz exatamente o gabarito e está totalmente apoiada no trecho [2], que contém a mesma informação com detalhes complementares verificados. |
| e se tiver defeito? _(com histórico)_ | ✅ | Correta | ✅ | 6,7 s | A resposta traz corretamente a informação principal do gabarito (até 90 dias) e expande com detalhes adicionais que estão integralmente apoiados nos trechos fornecidos, sem contradições ou informações inventadas. |
| e se a internet cair, ainda funciona? _(com histórico)_ | ✅ | Errada | ✅ | 6,1 s | Resposta principal ('Sim, funciona normalmente') contradiz o gabarito ('Não'). O gabarito indica que o sistema NÃO funciona normalmente porque PIX fica indisponível; o assistente deveria ter enfatizado essa limitação como resposta primária. |
| Qual é a capital da Austrália? | — | Recusa correta | — | 5,5 s | O assistente deixou claro que não encontrou a informação nos documentos e não inventou uma resposta, redirecionando adequadamente para tópicos cobertos. |
| A loja abre aos domingos? | — | Recusa correta | — | 6,7 s | O assistente deixou claro que não encontrou a informação nos documentos, distinguiu corretamente entre o horário do suporte técnico e o horário de funcionamento da loja, e sugeriu alternativas sem inventar resposta. |
| Qual o salário de um operador de caixa? | — | Recusa correta | — | 6,3 s | Assistente deixou explicitamente claro que não encontrou a informação nos documentos fornecidos, identificou quais documentos foram consultados, e não inventou uma resposta |
| Qual o prazo de entrega das compras feitas pelo site? | — | Recusa correta | — | 7,2 s | O assistente deixou explicitamente claro que não encontrou a informação nos documentos disponibilizados, não inventou uma resposta, e ofereceu alternativas onde a informação poderia ser encontrada. |

## Respostas completas

### troca-prazo-geral

**Pergunta:** Qual o prazo para trocar um produto?  
**Gabarito:** 30 dias corridos após a compra, com cupom fiscal ou número do pedido.  
**Resposta:** De acordo com a **Política de trocas e devoluções** fornecida:  ## Prazos para troca:  1. **Prazo geral**: O cliente pode trocar qualquer produto em até **30 dias corridos** após a compra, desde que apresente o cupom fiscal ou o número do pedido.  2. **Produtos eletrônicos**: Têm prazo reduzido de **7 dias** para troca por arrependimento.  3. **Produtos com defeito**: Podem ser devolvidos em até **90 dias**. A loja analisa o item em até 5 dias úteis e, confirmado o defeito, o cliente escolhe entre troca, crédito na loja ou estorno do valor.  **Condições importantes**: O produto deve estar sem sinais de uso, com etiqueta e na embalagem original. Peças íntimas, produtos de higiene e itens personalizados não podem ser trocados, exceto em caso de defeito de fabricação.

### troca-eletronico

**Pergunta:** Comprei um fone de ouvido e me arrependi. Até quando posso trocar?  
**Gabarito:** Eletrônicos têm 7 dias para troca por arrependimento.  
**Resposta:** De acordo com a **Política de trocas e devoluções** da loja:  **Você pode trocar o fone de ouvido em até 7 dias** após a compra, já que produtos da linha de eletrônicos têm esse prazo específico para troca por arrependimento.  **Condições necessárias:** - Apresentar o cupom fiscal ou número do pedido - O produto deve estar sem sinais de uso - Com etiqueta e na embalagem original  Se o fone apresentar um defeito de fabricação (não apenas arrependimento), você teria até **90 dias** para devolver, e a loja analisaria o item em até 5 dias úteis.

### troca-defeito

**Pergunta:** Qual o prazo para devolver um produto com defeito de fabricação?  
**Gabarito:** Até 90 dias; a loja analisa em até 5 dias úteis.  
**Resposta:** De acordo com a **Política de trocas e devoluções**, o prazo para devolver um produto com defeito de fabricação é de **até 90 dias**.  Após a devolução, a loja analisa o item em até 5 dias úteis. Confirmado o defeito, o cliente pode escolher entre as seguintes opções:  - **Troca** do produto - **Crédito na loja** - **Estorno** do valor

### troca-peca-intima

**Pergunta:** Posso trocar uma peça íntima que não serviu?  
**Gabarito:** Não, peças íntimas só podem ser trocadas em caso de defeito de fabricação.  
**Resposta:** Conforme a **Política de trocas e devoluções**, **peças íntimas não podem ser trocadas**, com uma única exceção:  - **Exceção:** Peças íntimas podem ser devolvidas em caso de **defeito de fabricação**  Se a peça íntima que não serviu não apresenta defeito de fabricação, infelizmente não é possível realizar a troca. A política especifica que peças íntimas, assim como produtos de higiene e itens personalizados, estão fora da política de troca por arrependimento.  Se você acredita que há um defeito de fabricação no produto, você pode solicitar a devolução em até **90 dias** após a compra apresentando o cupom fiscal ou número do pedido. A loja analisará o item em até 5 dias úteis e, confirmado o defeito, você poderá escolher entre troca, crédito na loja ou estorno do valor.

### estorno-pix

**Pergunta:** Paguei com PIX. Em quanto tempo recebo o estorno?  
**Gabarito:** Em até 2 dias úteis, na mesma conta de origem.  
**Resposta:** De acordo com a política de trocas e devoluções da loja:  **Pagamentos via PIX são devolvidos em até 2 dias úteis para a mesma conta de origem.**  O estorno será depositado diretamente na conta bancária que você usou para fazer o pagamento via PIX.

### estorno-cartao

**Pergunta:** Quando o estorno no cartão de crédito aparece?  
**Gabarito:** Em até duas faturas.  
**Resposta:** De acordo com a política de trocas e devoluções, **o estorno no cartão de crédito aparece em até duas faturas**.

### estorno-dinheiro

**Pergunta:** Como funciona a devolução de uma compra paga em dinheiro?  
**Gabarito:** Gera crédito na loja ou devolução no caixa, com autorização do gerente.  
**Resposta:** De acordo com a **Política de trocas e devoluções**, para compras pagas em dinheiro:  **Compras em dinheiro geram crédito na loja ou devolução no caixa, com autorização do gerente.**  Isso significa que o cliente tem duas opções:  1. **Crédito na loja** — recebe o valor em forma de crédito para usar em futuras compras 2. **Devolução no caixa** — recebe o dinheiro de volta diretamente, mas isso requer autorização do gerente  A devolução está vinculada às políticas gerais de troca e devoluções: - **Prazo de 30 dias corridos** após a compra para devoluções normais (com cupom fiscal ou número do pedido) - **Prazo de 90 dias** para produtos com defeito de fabricação - O produto deve estar sem sinais de uso, com etiqueta e na embalagem original

### caixa-fundo-troco

**Pergunta:** Qual o valor padrão do fundo de troco na abertura do caixa?  
**Gabarito:** R$ 200,00.  
**Resposta:** De acordo com o contexto fornecido no **Manual do operador de caixa**, o valor padrão do fundo de troco na abertura do caixa é de **R$ 200,00**.  Conforme descrito na seção "Abertura do caixa": > "No início do turno, o operador faz login com seu código e senha e informa o valor do fundo de troco, que por padrão é de R$ 200,00."

### caixa-varias-unidades

**Pergunta:** Como lanço várias unidades do mesmo produto no PDV?  
**Gabarito:** Digita a quantidade, pressiona asterisco (*) e lê o código do produto.  
**Resposta:** Para lançar várias unidades do mesmo produto no PDV, siga estes passos:  1. **Digite a quantidade** desejada 2. **Pressione a tecla asterisco (*)** 3. **Leia o código do produto** (usando o leitor de código de barras ou digitando o código interno)  Dessa forma, o sistema registrará automaticamente a quantidade de unidades que você informou.

### caixa-cancelar-item

**Pergunta:** Como cancelo um item antes de finalizar a venda?  
**Gabarito:** Pressiona F6 e seleciona o item; acima de R$ 100,00 precisa da senha do supervisor.  
**Resposta:** Para cancelar um item antes de finalizar a venda:  1. **Pressione F6** 2. **Selecione o item** que deseja cancelar  ⚠️ **Importante:** Se o valor do item cancelado for **superior a R$ 100,00**, será necessária a **senha do supervisor** para confirmar o cancelamento.

### caixa-parcelamento

**Pergunta:** Em quantas vezes posso parcelar no cartão de crédito?  
**Gabarito:** Até 6 parcelas sem juros, para compras acima de R$ 300,00.  
**Resposta:** Segundo o contexto fornecido, é possível parcelar no cartão de crédito **em até 6 parcelas sem juros**, mas essa condição se aplica apenas para **compras acima de R$ 300,00**.

### caixa-formas-pagamento

**Pergunta:** Dá para pagar uma mesma compra com mais de uma forma de pagamento?  
**Gabarito:** Sim, até três formas diferentes na mesma venda.  
**Resposta:** Sim, é possível dividir o pagamento em até **três formas diferentes na mesma venda**.  As formas de pagamento aceitas pelo PDV são: - Dinheiro - Cartão de débito - Cartão de crédito em até 6 parcelas sem juros (para compras acima de R$ 300,00) - PIX via QR Code na tela

### caixa-sangria

**Pergunta:** Quando preciso fazer sangria do caixa?  
**Gabarito:** Quando passa de R$ 1.000,00 em dinheiro; o supervisor retira o excedente e registra com a senha dele.  
**Resposta:** Conforme informado no manual do caixa, **você precisa fazer sangria quando o caixa ultrapassa R$ 1.000,00 em dinheiro**.  Nesse caso: 1. O sistema exibe um alerta de sangria 2. Você deve chamar o supervisor 3. O supervisor retira o excedente e registra a sangria com sua senha

### caixa-fechamento

**Pergunta:** Como faço o fechamento do caixa no fim do turno?  
**Gabarito:** Pressiona F10, informa os valores contados e imprime o relatório; diferenças acima de R$ 5,00 exigem justificativa por escrito.  
**Resposta:** De acordo com o Manual do Operador de Caixa, o fechamento do caixa é feito da seguinte forma:  1. **Pressione F10** para iniciar o processo de fechamento 2. **Informe os valores contados** de cada forma de pagamento 3. **Imprima o relatório de fechamento**  **Importante:** Se houver diferenças acima de R$ 5,00 entre os valores contados e os registrados no sistema, será necessário fornecer uma justificativa por escrito.

### suporte-impressora

**Pergunta:** A impressora fiscal parou de imprimir, o que eu faço?  
**Gabarito:** Verificar se está ligada e com papel, testar em Configurações > Periféricos e, se falhar, reiniciar o serviço com Ctrl+Shift+P.  
**Resposta:** Com base no documento de suporte fornecido, aqui está o que você deve fazer:  1. **Verificar o básico**: Verifique se a impressora está ligada e se possui papel.  2. **Testar a impressora**: No PDV, acesse **Configurações > Periféricos** e clique em **"Testar impressora"**.  3. **Se o teste falhar**: Reinicie o serviço de impressão utilizando o atalho **Ctrl+Shift+P**.  Siga esses passos nessa ordem e o problema deve ser resolvido.

### suporte-sem-internet

**Pergunta:** O sistema caiu a internet. Consigo continuar vendendo?  
**Gabarito:** Sim, em modo contingência por até 24 horas; as vendas sincronizam depois, mas o PIX fica indisponível.  
**Resposta:** Sim, você consegue continuar vendendo mesmo com a queda de internet.   Segundo a documentação do sistema:  **O PDV funciona em modo contingência por até 24 horas sem internet.** As vendas ficam salvas localmente e são enviadas automaticamente ao servidor quando a conexão volta.  **Importante:** Pagamentos via PIX ficam indisponíveis no modo contingência, então você poderá usar apenas: - Dinheiro - Cartão de débito - Cartão de crédito

### suporte-preco

**Pergunta:** Como altero o preço de um produto?  
**Gabarito:** Só na retaguarda, em Produtos > Tabela de preços; chega aos caixas em até 10 minutos ou na hora com 'Sincronizar agora'.  
**Resposta:** # Como alterar o preço de um produto  Preços são alterados **apenas no sistema de retaguarda (back-office)**, seguindo estes passos:  1. Acesse o menu **Produtos > Tabela de preços** 2. Faça as alterações desejadas  ## Tempo de atualização nos caixas  - **Até 10 minutos** para a atualização chegar aos caixas automaticamente - **Imediatamente** se o operador usar a opção **"Sincronizar agora"** no PDV

### suporte-horario

**Pergunta:** Qual o horário de atendimento do suporte técnico?  
**Gabarito:** Segunda a sábado, das 8h às 20h.  
**Resposta:** De acordo com o contexto fornecido, **o suporte técnico atende de segunda a sábado, das 8h às 20h**, pelo telefone 0800 000 0000 (fictício) ou pelo chat dentro do próprio sistema.

### conversa-defeito

**Pergunta:** e se tiver defeito?  
**Gabarito:** Com defeito de fabricação, o prazo é de até 90 dias.  
**Resposta:** Com base no contexto fornecido, se o produto tiver **defeito de fabricação**:  ✓ **Prazo**: Você pode devolver em até **90 dias** após a compra  ✓ **Análise**: A loja analisa o item em até **5 dias úteis**  ✓ **Opções após confirmado o defeito** (você escolhe uma): - **Troca** por produto novo - **Crédito na loja** - **Estorno** do valor  ✓ **Reembolso** (depende da forma de pagamento): - **Cartão de crédito**: aparece em até duas faturas - **PIX**: devolvido em até 2 dias úteis para a mesma conta de origem - **Dinheiro**: crédito na loja ou devolução no caixa (com autorização do gerente)  Para mais detalhes ou orientação específica sobre seu caso, você pode entrar em contato com o **suporte técnico**: - 📞 Segunda a sábado, das 8h às 20h - 📱 Chat dentro do próprio sistema

### conversa-pix-offline

**Pergunta:** e se a internet cair, ainda funciona?  
**Gabarito:** Não: no modo contingência (sem internet) o PIX fica indisponível.  
**Resposta:** Sim, o sistema funciona normalmente se a internet cair.   De acordo com a documentação do suporte, o PDV possui um **modo contingência** que permite continuar vendendo por até 24 horas sem internet. As vendas realizadas ficam salvas localmente e são enviadas automaticamente ao servidor quando a conexão for restaurada.  **Importante:** Pagamentos via PIX ficam indisponíveis durante o modo contingência, mas você pode aceitar outras formas de pagamento (dinheiro, débito e crédito).

### fora-capital

**Pergunta:** Qual é a capital da Austrália?  
**Resposta:** Não consigo responder essa pergunta com base no contexto fornecido.   O contexto disponível contém apenas informações sobre políticas de trocas e devoluções, procedimentos de caixa (PDV), formas de pagamento e FAQ de suporte de uma loja. Não há nenhuma informação sobre geografia ou capitais de países.  Se você tiver dúvidas sobre as políticas de troca, procedimentos de caixa ou suporte da loja, posso ajudá-lo com essas informações!

### fora-domingo

**Pergunta:** A loja abre aos domingos?  
**Resposta:** Não encontro informação sobre o horário de funcionamento da loja nos domingos no contexto fornecido.  O documento de FAQ menciona que o **suporte técnico** atende de segunda a sábado (das 8h às 20h), mas isso refere-se apenas ao horário de atendimento do suporte, não ao funcionamento geral da loja.  Para saber se a loja abre aos domingos, você precisaria consultar um documento que contenha o horário de funcionamento completo da loja ou entrar em contato com o suporte técnico.

### fora-salario

**Pergunta:** Qual o salário de um operador de caixa?  
**Resposta:** Não encontrei informações sobre o salário de um operador de caixa no contexto fornecido.   Os documentos disponibilizados (Manual do operador de caixa, FAQ de suporte e Política de trocas) contêm orientações sobre procedimentos operacionais do sistema PDV, como abertura e fechamento de caixa, registro de vendas, formas de pagamento e políticas de troca, mas não incluem dados sobre remuneração ou salários.  Para obter essa informação, você precisaria consultar documentos de recursos humanos, política salarial ou contrato de trabalho da empresa.

### fora-entrega

**Pergunta:** Qual o prazo de entrega das compras feitas pelo site?  
**Resposta:** Não encontro informações sobre prazos de entrega para compras feitas pelo site no contexto disponibilizado.  O material fornecido inclui documentos sobre: - FAQ de suporte técnico - Política de trocas e devoluções - Manual do operador de caixa (PDV)  Mas nenhum deles aborda especificamente os prazos de entrega de pedidos realizados via e-commerce.  Para obter essa informação, você precisa consultar: - A seção de **Dúvidas Frequentes (FAQ) sobre entregas** ou **Termos de Serviço** do site - Entrar em contato com o **suporte técnico** (disponível de seg-sab, 8h às 20h, pelo telefone 0800 000 0000 ou chat do sistema)  Há algo mais que eu possa ajudar com as informações disponíveis?

