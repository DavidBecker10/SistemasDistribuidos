  import React, { useState, useEffect } from 'react';
  import { FiSearch } from 'react-icons/fi';
  import './styles.css';

  function App() {
      const userId = localStorage.getItem('userId') || (() => {
      const newId = crypto.randomUUID();
      localStorage.setItem('userId', newId);
      return newId;
    })();

    const [events, setEvents] = useState([]);
    const [destino, setDestino] = useState('');
    const [dataEmbarque, setDataEmbarque] = useState('');
    const [portoEmbarque, setPortoEmbarque] = useState('');
    const [resultados, setResultados] = useState([]);
    const [selecoes, setSelecoes] = useState({});
    const [reservas, setReservas] = useState([]);
    const [interessePromocoes, setInteressePromocoes] = useState(false);

    useEffect(() => {
        console.log(userId);
        const eventSource = new EventSource(`http://localhost:5000/sse?userId=${userId}`);

        eventSource.onopen = () => console.log(">>> Connection opened!");

        eventSource.addEventListener('pagamentoAprovado', (event) => {
            try {
              const parsedData = JSON.parse(event.data);
              console.log("🔔 Evento Aprovado recebido:", parsedData);
              
              setEvents((prev) => [
                ...prev,
                {
                  Tipo: "Pagamento",
                  StatusPagamento: "Pagamento Aprovado",
                  Id: parsedData.Id,
                  UserId: parsedData.UserId,
                  ItinerarioId: parsedData.ItinerarioId,  
                  Destino: parsedData.Destino,
                  DataEmbarque: parsedData.DataEmbarque,
                  NumeroCabines: parsedData.NumeroCabines,
                }
              ]);
              handleSearch();
              fetchReservas();

            } catch (err) {
              console.error("Erro ao parsear event.data:", event.data, err);
            }
        });

        eventSource.addEventListener('pagamentoRecusado', (event) => {
            try {
              const parsedData = JSON.parse(event.data);
              console.log("🔔 Evento Recusado recebido:", parsedData);

              setEvents((prev) => [
                ...prev,
                {
                  Tipo: "Pagamento",
                  StatusPagamento: "Pagamento Recusado",
                  Id: parsedData.Id,
                  UserId: parsedData.UserId,
                  ItinerarioId: parsedData.ItinerarioId,  
                  Destino: parsedData.Destino,
                  DataEmbarque: parsedData.DataEmbarque,
                  NumeroCabines: parsedData.NumeroCabines,
                }
              ]);
              handleSearch();
              fetchReservas();

            } catch (err) {
              console.error("Erro ao parsear event.data:", event.data, err);
            }
        });

        eventSource.addEventListener('promocao', (event) => {
            try {
                const parsedData = JSON.parse(event.data);
                console.log("🔔 Promoção recebida:", parsedData);

                // Atualizar o estado ou notificar o usuário sobre a promoção
                setEvents((prev) => [
                    ...prev,
                    {
                        Tipo: "Promoção",
                        Mensagem: parsedData, // Promoção completa como string
                    }
                ]);

            } catch (err) {
                console.error("Erro ao parsear evento de promoção:", event.data, err);
            }
        });

                eventSource.addEventListener('bilheteGerado', (event) => {
            try {
                const parsedData = JSON.parse(event.data);
                console.log("🔔 Bilhete Gerado recebido:", parsedData);

                // Atualizar o estado ou notificar o usuário sobre o bilhete gerado
                setEvents((prev) => [
                    ...prev,
                    {
                        Tipo: "bilheteGerado",
                        Mensagem: `Bilhete gerado para o itinerário ${parsedData.OriginalMessage.ItinerarioId}.`,
                    }
                ]);

            } catch (err) {
                console.error("Erro ao parsear evento de bilhete gerado:", event.data, err);
            }
        });

        eventSource.onerror = (err) => {
            console.error('Erro no SSE:', err);
            eventSource.close();
        };

        return () => {
            eventSource.close();
        };
    }, []);

    // Busca de itinerarios pelos campos
    const handleSearch = async () => {
      try {
        const response = await fetch('http://localhost:5000/api/itinerarios');
        const itinerarios = await response.json();
        const resultadosFiltrados = itinerarios.filter((itinerario) => {
          const correspondeDestino = destino === '' || itinerario.PortoDesembarque.toLowerCase().includes(destino.toLowerCase());
          const correspondeData = dataEmbarque === '' || itinerario.DatasPartida.includes(dataEmbarque);
          const correspondePorto = portoEmbarque === '' || itinerario.PortoEmbarque.toLowerCase().includes(portoEmbarque.toLowerCase());
          return correspondeDestino && correspondeData && correspondePorto;
        });
        setResultados(resultadosFiltrados);
      } catch (error) {
        console.error('Erro ao buscar itinerários:', error);
        alert('Erro ao buscar itinerários. Tente novamente mais tarde.');
      }
    };

    const handlePayment = (paymentId) => {
      fetch(`http://localhost:5003/api/pagamento/processar/${paymentId}`, { method: 'POST' })
        .then((response) => response.json())
        .then((data) => {
          alert(`Pagamento ${data.status} para ID: ${data.paymentId}`);
        })
        .catch((error) => console.error("Erro ao processar pagamento:", error));
    };

    const fetchReservas = async () => {
      try {
        const response = await fetch(`http://localhost:5000/api/reservas`);
        const todasReservas = await response.json();
        setReservas(todasReservas.filter((reserva) => reserva.UserId === userId));
      } catch (error) {
        console.error('Erro ao buscar reservas:', error);
      }
    };

    // Metodo de selecao de campos
    const handleSelectChange = (id, campo, valor) => {
      setSelecoes((prevSelecoes) => ({
        ...prevSelecoes,
        [id]: {
          ...prevSelecoes[id],
          [campo]: valor,
        },
      }));
    };

    // Requisicao para criar reserva
    const handleSelect = async (itinerario) => {
      const selecao = selecoes[itinerario.Id];

      if (!selecao || !selecao.dataSelecionada) {
        alert('Por favor, selecione uma data de partida.');
        return;
      }

      try {
        const novaReserva = {
          Id: crypto.randomUUID(),
          UserId: userId,
          Destino: itinerario.PortoDesembarque,
          DataEmbarque: selecao.dataSelecionada,
          NumeroCabines: selecao.numeroCabines || 1,
          ItinerarioId: itinerario.Id,
        };

        const response = await fetch('http://localhost:5000/api/reserva/criar', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
          },
          body: JSON.stringify(novaReserva),
        });

        if (response.ok) {
          alert('Reserva criada com sucesso!');
          handleSearch();
          fetchReservas();
        } else {
          alert('Erro ao criar reserva.');
        }
      } catch (error) {
        console.error('Erro ao enviar reserva:', error);
        alert('Erro ao enviar reserva. Tente novamente mais tarde.');
      }
    };

    const handleCancel = async (reservaId) => {
      try {
        const response = await fetch('http://localhost:5000/api/reserva/cancelar', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ Id: reservaId }),
        });

        if (response.ok) {
          alert('Reserva cancelada com sucesso!');
          handleSearch();
          fetchReservas();
        } else {
          alert(response.status);
          alert('Erro ao cancelar reserva.');
        }
      } catch (error) {
        console.error('Erro ao cancelar reserva:', error);
        alert('Erro ao cancelar reserva. Tente novamente mais tarde.');
      }
    };

    useEffect(() => {
      fetchReservas();
    }, []);

    const atualizarInteressePromocoes = async (interesse) => {
      try {
        const response = await fetch('http://localhost:5000/api/promocoes', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({
            UserId: userId,
            Interested: interesse,
          }),
        });

        if (!response.ok) {
          alert('Erro ao atualizar preferência de promoções.');
        } else {
          alert('Preferência atualizada com sucesso!');
        }
      } catch (error) {
        console.error('Erro ao atualizar preferência de promoções:', error);
        alert('Erro ao comunicar com o servidor. Tente novamente mais tarde.');
      }
    };

    const handlePromocoesChange = (event) => {
      const novoValor = event.target.checked;
      setInteressePromocoes(novoValor);
      atualizarInteressePromocoes(novoValor);
    };


    return (
      <div className="container">
        <h1 className="title">Reserva de Cruzeiros</h1>

        <div className="containerInput">
          <input
            type="text"
            placeholder="Destino..."
            value={destino}
            onChange={(e) => setDestino(e.target.value)}
          />

          <input
            type="text"
            placeholder="Data embarque..."
            value={dataEmbarque}
            onChange={(e) => setDataEmbarque(e.target.value)}
          />

          <input
            type="text"
            placeholder="Porto embarque..."
            value={portoEmbarque}
            onChange={(e) => setPortoEmbarque(e.target.value)}
          />

          <button className="buttonSearch" onClick={handleSearch}>
            <FiSearch size={25} color="#FFF" />
          </button>
        </div>

        <div className="promocoes">
          <label>
            <input
              type="checkbox"
              checked={interessePromocoes}
              onChange={handlePromocoesChange}
            />
            Quero receber promoções
          </label>
        </div>

        <div className="results">
          {resultados.length > 0 ? (
            <table>
              <thead>
                <tr>
                  <th>Datas de Partida</th>
                  <th>Navio</th>
                  <th>Porto de Embarque</th>
                  <th>Porto de Desembarque</th>
                  <th>Lugares Visitados</th>
                  <th>Noites</th>
                  <th>Preço (por pessoa)</th>
                  <th>Ações</th>
                  <th>Cabines Disponíveis</th>
                </tr>
              </thead>
              <tbody>
                {resultados.map((itinerario) => (
                  <tr key={itinerario.id}>
                    <td>{itinerario.DatasPartida.join(', ')}</td>
                    <td>{itinerario.NomeNavio}</td>
                    <td>{itinerario.PortoEmbarque}</td>
                    <td>{itinerario.PortoDesembarque}</td>
                    <td>{itinerario.LugaresVisitados.join(', ')}</td>
                    <td>{itinerario.NumeroNoites}</td>
                    <td>${itinerario.ValorPorPessoa}</td>
                    <td>
                      <div>
                        <label>
                          Data:
                          <select className="dataReserva"
                            value={selecoes[itinerario.Id]?.dataSelecionada || ''}
                            onChange={(e) => handleSelectChange(itinerario.Id, 'dataSelecionada', e.target.value)}
                          >
                            <option value="">Selecione</option>
                            {itinerario.DatasPartida.map((data, idx) => (
                              <option key={idx} value={data}>{data}</option>
                            ))}
                          </select>
                        </label>
                        <label>
                          Cabines:
                          <input className="numCabines"
                            type="number"
                            min="1"
                            value={selecoes[itinerario.Id]?.numeroCabines || 1}
                            onChange={(e) => handleSelectChange(itinerario.Id, 'numeroCabines', parseInt(e.target.value, 10) || 1)}
                          />
                        </label>
                        <button onClick={() => handleSelect(itinerario)}>Reservar</button>
                      </div>
                    </td>
                    <td>{itinerario.CabinesDisponiveis}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p>Nenhum itinerário encontrado.</p>
          )}
        </div>

        <div className="reservas">
          <h2>Suas Reservas</h2>
          {reservas.length > 0 ? (
            <ul className="reservasLista">
              {reservas.map((reserva) => (
                <li key={reserva.Id}>
                  <span>{`Destino: ${reserva.Destino}, Data: ${reserva.DataEmbarque}, Cabines: ${reserva.NumeroCabines}`}</span>
                  <button onClick={() => handleCancel(reserva.Id)}>Cancelar</button>
                  <button onClick={() => handlePayment(reserva.paymentId)}>Pagar</button>
                </li>
              ))}
            </ul>
          ) : (
            <p>Você não possui reservas.</p>
          )}
        </div>
        <div className="eventos">
            <h1>Eventos Recebidos</h1>
            <ul>
                {events.map((event, index) => (
                    <li key={index}>
                        {event.Tipo === "Promoção" ? (
                            <>
                                <span style={{ color: "blue" }}>[Promoção]</span> {event.Mensagem}
                            </>
                        ) : event.Tipo === "bilheteGerado" ? (
                            <>
                                <span style={{ color: "orange" }}>[Bilhete Gerado]</span> {event.Mensagem}
                            </>
                        ) : event.StatusPagamento ? (
                            <>
                                <span
                                    style={{
                                        color: event.StatusPagamento === "Pagamento Aprovado" ? "green" : "red",
                                    }}
                                >
                                    [{event.StatusPagamento}]
                                </span>
                                {event.Destino && ` Destino: ${event.Destino}`}
                                {event.DataEmbarque && `, Data de Embarque: ${event.DataEmbarque}`}
                                {event.NumeroCabines && `, Cabines: ${event.NumeroCabines}`}
                            </>
                        ) : (
                            console.log("Evento desconhecido:", event)
                        )}
                    </li>
                ))}
            </ul>
        </div>
      </div>
    );
  }

  export default App;