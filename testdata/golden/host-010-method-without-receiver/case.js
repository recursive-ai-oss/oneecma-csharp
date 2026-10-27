// A method taken off its object loses `this`.
const getOrder = api.getOrder;
getOrder(1);
