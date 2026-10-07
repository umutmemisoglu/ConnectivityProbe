// Monitor arayüzündeki alaka sıralı aramanın testleri (Node'un yerleşik test çalıştırıcısı): node --test tests/monitor-ui
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const Search = require('../../src/ConnectivityProbe.Monitor/wwwroot/search.js');

const connections = [
  ['ELMT1EntSQL_RO', 'ELMT1EntSQL_RO.db.example.lan', 1433, 'Orders API'],
  ['ELTstVasSql1', 'ELTstVasSql1.db.example.lan', 1433, ''],
  ['Ana veritabanı', 'sql01.corp.local', 1433, 'Fatura'],
  ['Redis önbellek', 'redis-master', 6379, 'Orders API Sample'],
  ['Ödeme API', 'https://payments.example.com', null, ''],
  ['Kafka', 'kafka-0.kafka', 9092, ''],
  ['RabbitMQ', 'rabbit.svc', 5672, 'ECM'],
  ['GitHub', 'https://github.com/', null, ''],
];
const entries = connections.map(([name, host, port, apps]) => ({ item: name, fields: [
  { value: name, weight: 3 }, { value: host, weight: 2 }, { value: port, weight: 1.5, exact: true }, { value: apps, weight: 1 },
] }));
const find = (q) => Search.rank(q, entries).map((r) => r.item);

test('birebir ad en üstte', () => assert.equal(find('kafka')[0], 'Kafka'));
test('yazım hatası tolere edilir', () => {
  assert.deepEqual(find('redsi'), ['Redis önbellek']);
  assert.deepEqual(find('rabit'), ['RabbitMQ']);
  assert.deepEqual(find('gthub'), ['GitHub']);
});
test('Türkçe karakter olmadan bulunur', () => {
  assert.deepEqual(find('odeme'), ['Ödeme API']);
  assert.deepEqual(find('veritabani'), ['Ana veritabanı']);
});
test('birleşik adlar parçalanır (ELMT1EntSQL_RO -> elmt, ent, sql, ro)', () => assert.equal(find('ent sql')[0], 'ELMT1EntSQL_RO'));
test('port birebir eşleşir', () => assert.deepEqual(find('6379'), ['Redis önbellek']));
test('kullanan uygulamanın adıyla bulunur', () => assert.ok(find('orders').includes('ELMT1EntSQL_RO')));
test('sayısal sorguda her parça eşleşmeli', () => assert.deepEqual(find('1433 6379'), []));
test('alakasız sorgu boş döner', () => assert.deepEqual(find('zzzzqq'), []));
test('Türkçe karakterli metinde eşleşme işaretlenir', () =>
  assert.equal(Search.highlight('Ödeme API', 'odeme', (x) => x), '<mark>Ödeme</mark> API'));
test('kelime bölme', () => assert.deepEqual(Search.tokens('ELMT1EntSQL_RO').slice(0, 5), ['elmt', '1', 'ent', 'sql', 'ro']));
