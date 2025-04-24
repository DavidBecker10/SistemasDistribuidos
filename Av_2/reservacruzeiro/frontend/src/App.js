import React, { useState, useEffect } from 'react';
import { FiSearch } from 'react-icons/fi';
import './styles.css';

function App() {
  const [destino, setDestino] = useState('');
  const [dataEmbarque, setDataEmbarque] = useState('');
  const [portoEmbarque, setPortoEmbarque] = useState('');
  const [resultados, setResultados] = useState([]);
  const [selecoes, setSelecoes] = useState({});
  const [statusPagamentos, setStatusPagamentos] = useState([]);

  // Busca de itinerarios pelos campos
  const handleSearch = async () => {
    try {
      const response = await fetch('http://localhost:5000/api/itinerarios');
      const itinerarios = await response.json();
      const resultadosFiltrados = itinerarios.filter((itinerario) => {
        const correspondeDestino = destino === '' || itinerario.portoDesembarque.toLowerCase().includes(destino.toLowerCase());
        const correspondeData = dataEmbarque === '' || itinerario.datasPartida.includes(dataEmbarque);
        const correspondePorto = portoEmbarque === '' || itinerario.portoEmbarque.toLowerCase().includes(portoEmbarque.toLowerCase());
        return correspondeDestino && correspondeData && correspondePorto;
      });
      setResultados(resultadosFiltrados);
    } catch (error) {
      console.error('Erro ao buscar itinerários:', error);
      alert('Erro ao buscar itinerários. Tente novamente mais tarde.');
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
    const selecao = selecoes[itinerario.id];

    if (!selecao || !selecao.dataSelecionada) {
      alert('Por favor, selecione uma data de partida.');
      return;
    }

    try {
      const response = await fetch('http://localhost:5000/api/reserva/criar', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          ItinerarioId: itinerario.id,
          NomeCliente: 'Cliente Teste',
          NumeroPassageiros: selecao.numeroPassageiros || 1,
          DataReserva: selecao.dataSelecionada,
        }),
      });

      if (response.ok) {
        alert('Reserva criada com sucesso!');
      } else {
        alert('Erro ao criar reserva.');
      }
    } catch (error) {
      console.error('Erro ao enviar reserva:', error);
      alert('Erro ao enviar reserva. Tente novamente mais tarde.');
    }
  };

  // Requisicao para buscar o status do pagamento
  const fetchStatusPagamentos = async () => {
    try {
      const response = await fetch('http://localhost:5000/api/pagamento/status');
      const status = await response.json();
      setStatusPagamentos(status);
    } catch (error) {
      console.error('Erro ao buscar status de pagamentos:', error);
    }
  };

  // Atualizar status pagamento a cada 10 segundos
  useEffect(() => {
    const interval = setInterval(fetchStatusPagamentos, 10000);
    return () => clearInterval(interval);
  }, []);

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
              </tr>
            </thead>
            <tbody>
              {resultados.map((itinerario) => (
                <tr key={itinerario.id}>
                  <td>{itinerario.datasPartida.join(', ')}</td>
                  <td>{itinerario.nomeNavio}</td>
                  <td>{itinerario.portoEmbarque}</td>
                  <td>{itinerario.portoDesembarque}</td>
                  <td>{itinerario.lugaresVisitados.join(', ')}</td>
                  <td>{itinerario.numeroNoites}</td>
                  <td>${itinerario.valorPorPessoa}</td>
                  <td>
                    <div>
                      <label>
                        Data:
                        <select className="dataReserva"
                          value={selecoes[itinerario.id]?.dataSelecionada || ''}
                          onChange={(e) => handleSelectChange(itinerario.id, 'dataSelecionada', e.target.value)}
                        >
                          <option value="">Selecione</option>
                          {itinerario.datasPartida.map((data, idx) => (
                            <option key={idx} value={data}>{data}</option>
                          ))}
                        </select>
                      </label>
                      <label>
                        Passageiros:
                        <input className="numPassageiros"
                          type="number"
                          min="1"
                          value={selecoes[itinerario.id]?.numeroPassageiros || 1}
                          onChange={(e) => handleSelectChange(itinerario.id, 'numeroPassageiros', parseInt(e.target.value, 10) || 1)}
                        />
                      </label>
                      <button onClick={() => handleSelect(itinerario)}>Reservar</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <p>Nenhum itinerário encontrado.</p>
        )}
      </div>

      <div className="paymentStatus">
        <h2>Status dos Pagamentos</h2>
        {statusPagamentos.length > 0 ? (
          <ul className='eachStatus'>
            {statusPagamentos.map((status, index) => (
              <li key={index}>{status}</li>
            ))}
          </ul>
        ) : (
          <p>Nenhum status de pagamento disponível.</p>
        )}
      </div>
    </div>
  );
}

export default App;