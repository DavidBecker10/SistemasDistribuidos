# Resumo Saga

    - Padrão de projeto usado para gerenciar transações distribuídas de forma assíncrona.
    - Isso é especialmente importante porque, ao contrário de sistemas monolíticos, onde tudo acontece em um único banco de dados ou sistema, nos microserviços cada serviço gerencia seu próprio estado e banco de dados.
    - Divide uma transação global em várias transações locais — cada uma realizada por um microserviço específico. A ideia é que:
        - Cada microserviço executa sua parte da transação.
        - Se todos tiverem sucesso, a operação completa é considerada bem-sucedida.
        - Se alguma parte falhar, os microserviços que já concluíram suas etapas devem executar ações compensatórias (como reverter ou corrigir suas alterações) para manter a consistência eventual do sistema.
    - Orquestração ou Coreografia

# Abstract

    - Microserviços orientados a eventos usam eventos para a comunicação entre os microserviços.
    - Modelo de comunicação assíncrona.
    - Implementar sagas em microserviços orientados a eventos é ainda mais trabalhoso devido à natureza assíncrona das interações.
    - Se recomenda o uso de frameworks bem testados para implementar sagas, em vez de construí-las do zero.
    - Este artigo faz um levantamento de alguns frameworks disponíveis para implementação de sagas em três plataformas populares: Java, Python e NodeJS.
    - Identifica a necessidade de uma abstração independente de fornecedor (vendor-agnostic) para separar a camada de transação da camada de negócio.

# Introduction

    - Demanda por escalabilidade vem crescendo de forma incessante.
    - Transações espalhadas por vários microserviços.
    - Padrão Saga para resolver o problema das transações distribuídas em microserviços.
    - Uso de frameworks bem testados e bem documentados.
    - Análise qualitativa dos frameworks disponíveis para implementar transações distribuídas assíncronas usando o padrão Saga em três plataformas populares de microserviços, a saber: Java, Python e NodeJS.

# Methodology

    - Primeira etapa – Revisão sistemática da literatura: Conduzimos uma pesquisa sistemática da literatura para obter uma compreensão mais profunda dos conceitos e problemas associados ao tema.
    - Segunda etapa – Desenvolvimento de um problema de referência: Para isso, reunimos estudos de caso e problemas relatados por profissionais da indústria, principalmente de fontes online como o StackOverflow e newsletters como o InfoQ. Implementamos essa arquitetura em todas as três plataformas (Java, Python e NodeJS) usando o Apache Kafka com a configuração necessária. Construímos os serviços sem utilizar nenhum framework de Saga, o que nos deu a oportunidade de estabelecer expectativas sobre o que esperar dos frameworks.
    - Terceira e última etapa – Análise dos frameworks: Realizamos uma pesquisa sobre os frameworks com base nas expectativas derivadas na etapa anterior.
    - Questões Específicas:
        - Quais são os frameworks de Saga disponíveis em cada plataforma?
        - Quais são os méritos e deméritos de cada framework?
        - Qual é a facilidade de migração de um framework para outro?

# Systematic Literature Survey

    - Resumo de vários artigos que falam sobre o tema.
    - Larrucea, Xabier, et al. [4] abordaram transações distribuídas em microserviços de maneira geral, explicando diversas estratégias para a migração de sistemas monolíticos para a arquitetura de microserviços. Eles sugeriram o uso do padrão Saga para alcançar consistência eventual.
    - Michael Müller [5] comparou três abordagens: bancos de dados compartilhados, orquestração e coreografia. O artigo desaconselha o uso de bancos compartilhados para garantir consistência, pois isso compromete a autonomia dos microserviços.
    - Rudrabhatla, Chaitanya K [15] apresentou uma análise quantitativa entre orquestração e coreografia. O artigo recomenda usar coreografia quando há poucos microserviços participantes, para melhor desempenho, e orquestração nos demais casos.
    - Xue, Gang, et al. [19] propuseram um mecanismo de coordenação baseado em Sagas para microserviços compostos descentralizados, com foco em alcançar consenso em tempo de execução. O estudo explicou como implementar transações compensatórias dentro de uma Saga, através de recuperação direta (forward) e recuperação reversa (backward), mas deixou a implementação real a cargo das aplicações.
    - Rajasekharaiah, Chandra [21] explicou diferentes maneiras de lidar com erros na execução de Sagas:
        - (i) ignorando o erro,
        - (ii) compensando o erro de forma imediata, e
        - (iii) compensando o erro posteriormente.
    - Megargel, Alan, Christopher M. Poskitt, e Venky Shankararaman [22] discutiram os prós e contras da coreografia e da orquestração em termos de acoplamento, verbosidade na comunicação, visibilidade e design. Eles desenvolveram um framework de decisão para escolher entre os dois padrões na implementação de Sagas, sugerindo até mesmo um modelo híbrido quando não é possível decidir por apenas um.
    - Sagas orientadas a eventos são fundamentais para garantir a consistência eventual na arquitetura de microserviços.

# Reference Architecture

    - Serviço de processamento de pedidos comum em sistemas de e-commerce.
    - O fluxo de trabalho típico do sistema:
        - Cliente pesquisa produtos. O sistema apresenta uma lista de produtos com suas especificações.
        - O cliente faz um pedido escolhendo um produto e a quantidade.
        - O sistema de gerenciamento de pedidos reserva o produto para o cliente e processa o pagamento.
        - Se o pagamento for concluído, o sistema confirma o pedido para o cliente com um ID de pedido.
        - Em caso de falha no pagamento, o sistema libera os produtos reservados para o estoque disponível.
    - Todos os dados, como estoque, informações do cliente e etc estão em um único banco de dados.
    - Dificuldade em manter e escalar esses sistemas monolíticos e, por isso, seguem uma abordagem orientada a domínios para decompor o monólito em um conjunto de microsserviços.
    - Sistema de e-commerce monolítico decomposto em vários microsserviços, sendo o Serviço de Pedidos, o Serviço de Inventário e o Serviço de Carteira os mais relevantes para esta discussão.
    - Esse cenário de criação de pedido é perfeito para implementar o padrão Saga:
        - Transações Distribuídas;
        - Necessidade de Consistência Eventual (uso de compensação);
        - Fluxos Orquestrados ou Coreografados.
    - Modelo inicial (Fig. 1): Um orquestrador de Saga comanda os outros serviços para realizarem transações locais e mantém o estado da Saga em seu próprio banco de dados. Ele também comanda os serviços a executarem transações compensatórias, quando necessário.
        - Limitação: interações síncronas entre os microsserviços.
    - Novo modelo (Fig. 2): Refatoração da arquitetura ao longo das linhas de microsserviços orientados a eventos para superar essa limitação.
    - A colaboração interna, anteriormente baseada em REST HTTP, foi substituída por eventos. Isso permitiu que os serviços colaborassem de forma assíncrona e não bloqueante.
    - Expectativas sobre os frameworks:
        - Suporte nas três plataformas escolhidas;
        - Suporte à orquestração;
        - Suporte à coreografia;
        - Suporte a brokers de mensagens plugáveis;
    - Procuraram vários frameworks que atendessem a essas expectativas.

# Results

    - Muitos frameworks para Java, pois é uma linguagem mais antiga e consolidada.
    - Poucos frameworks disponíveis para implementar o padrão Saga em Python e NodeJs.
    - Frameworks:
        - Axon e Eventuate Tram: orquestração e coreografia orientada a eventos, ideais para aplicações que seguem o padrão CQRS.
        - Nexflix Conductor: desenvolvido em Java, mas também oferece suporte para clientes Python. DSL baseada em JSON para definir orquestrações.
        - Spring Boot e Vert.x: Sem suporte nativo para sagas, mas com boa integração com várias tecnologias de bancos de dados, message broker. Podem ser utilizados em conjunto com outros frameworks.
        - Saga framework: único framework disponível para Python com suporte para sagas. Lançado recentemente (2021), ainda não encontrou ampla adoção.
        - Node-sagas: um dos poucos frameworks disponíveis para NodeJs. Lançado em 2020, ainda carece de muitos recursos necessários para arquiteturas orientadas a eventos.

# Conclusion and future scope

    - Complexidade no gerenciamento de transações distribuídas em microsserviços orientados a eventos. Portanto, necessidade de um framework para implementar o padrão Saga é evidente.
    - Bom número de frameworks para Java, mas poucos promissores para Python e NodeJs.
    - Diferentes frameworks impõem modelos arquiteturais distintos aos microsserviços participantes. Por causa disso, torna-se quase impossível migrar de um framework para outro.
    - O cenário atual não atende adequadamente à necessidade de implementar transações distribuídas assíncronas.
    - Necessidade de métodos de implementação de Sagas que sejam agnósticos a frameworks, agnósticos a fornecedores e agnósticos a plataformas.
    - Trabalhos futuros: pesquisar a possibilidade de construir uma abstração leve e declarativa de Saga para microsserviços orientados a eventos.
